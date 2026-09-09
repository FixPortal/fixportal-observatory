using System.Security.Cryptography;
using AiObservatory.Data.Security;
using AwesomeAssertions;

namespace AiObservatory.Data.Tests.Security;

// Shares the process-wide SLACK_WEBHOOK_PROTECTION_KEY with the repository tests: this class
// asserts no-key behaviour while a sibling sets the variable, so both are serialised into one
// non-parallel collection (see NotificationSettingsRepositoryTests).
[Collection("SlackWebhookProtectionKey")]
public class SlackWebhookProtectorTests
{
    private const string WebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz";
    private readonly SlackWebhookProtector _protector = new("test-passphrase");

    [Fact]
    public void Protect_Unprotect_round_trips_the_original_url()
    {
        _protector.Unprotect(_protector.Protect(WebhookUrl)).Should().Be(WebhookUrl);
    }

    [Fact]
    public void Protect_never_stores_the_recognisable_url()
    {
        var stored = _protector.Protect(WebhookUrl);

        stored.Should().StartWith(SlackWebhookProtector.EncryptedPrefix);
        stored.Should().NotContain("hooks.slack.com");
    }

    [Fact]
    public void Protect_uses_a_fresh_nonce_per_call()
    {
        _protector.Protect(WebhookUrl).Should().NotBe(_protector.Protect(WebhookUrl));
    }

    [Fact]
    public void Unprotect_returns_legacy_plaintext_unchanged()
    {
        // Rows written before SLACK_WEBHOOK_PROTECTION_KEY was set carry no prefix.
        _protector.Unprotect(WebhookUrl).Should().Be(WebhookUrl);
    }

    [Fact]
    public void Unprotect_throws_on_tampered_ciphertext()
    {
        var stored = _protector.Protect(WebhookUrl).ToCharArray();
        // Flip a character inside the base64 payload (never the trailing padding), so the
        // blob still decodes but the GCM tag check must reject it.
        var middle = stored.Length / 2;
        stored[middle] = stored[middle] == 'A' ? 'B' : 'A';

        var act = () => _protector.Unprotect(new string(stored));

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Unprotect_throws_under_a_different_key()
    {
        var stored = _protector.Protect(WebhookUrl);

        var act = () => new SlackWebhookProtector("another-passphrase").Unprotect(stored);

        act.Should().Throw<CryptographicException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Ctor_rejects_a_missing_passphrase(string? passphrase)
    {
        var act = () => new SlackWebhookProtector(passphrase!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UnprotectValue_returns_the_sentinel_for_an_encrypted_value_when_no_key_is_configured()
    {
        // The test host never sets SLACK_WEBHOOK_PROTECTION_KEY (integration tests rely on
        // the no-key pass-through). The facade runs inside EF materialisation, where a throw
        // would make the whole NotificationSettings row unreadable and take email alerting
        // down with Slack -- so the read path degrades to a sentinel, and the loud failure
        // belongs to the Slack notifier that actually needs the plaintext.
        Environment.GetEnvironmentVariable(SlackWebhookProtector.KeyEnvironmentVariable).Should().BeNull();

        SlackWebhookProtector
            .UnprotectValue(SlackWebhookProtector.EncryptedPrefix + "AAAA")
            .Should()
            .Be(SlackWebhookProtector.UndecryptableSentinel);
    }

    [Fact]
    public void UnprotectValue_passes_plaintext_through_when_no_key_is_configured()
    {
        Environment.GetEnvironmentVariable(SlackWebhookProtector.KeyEnvironmentVariable).Should().BeNull();

        SlackWebhookProtector.UnprotectValue(WebhookUrl).Should().Be(WebhookUrl);
    }

    [Fact]
    public void UnprotectValue_returns_the_sentinel_under_a_rotated_key()
    {
        // A key that no longer matches the one a value was encrypted under degrades exactly
        // like a missing key -- the row must still materialise.
        var stored = new SlackWebhookProtector("original-passphrase").Protect(WebhookUrl);
        Environment.SetEnvironmentVariable(SlackWebhookProtector.KeyEnvironmentVariable, "rotated-passphrase");
        try
        {
            SlackWebhookProtector.UnprotectValue(stored).Should().Be(SlackWebhookProtector.UndecryptableSentinel);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SlackWebhookProtector.KeyEnvironmentVariable, null);
        }
    }

    [Theory]
    [InlineData(SlackWebhookProtector.UndecryptableSentinel, true)]
    [InlineData(WebhookUrl, false)]
    [InlineData(null, false)]
    public void IsUndecryptable_identifies_only_the_sentinel(string? value, bool expected)
    {
        SlackWebhookProtector.IsUndecryptable(value).Should().Be(expected);
    }
}
