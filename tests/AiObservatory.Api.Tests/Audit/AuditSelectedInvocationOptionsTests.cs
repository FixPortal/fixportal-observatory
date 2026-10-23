using AiObservatory.Api.Audit;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Api.Tests.Audit;

public sealed class AuditSelectedInvocationOptionsTests
{
    [Fact]
    public void DefaultsAreUnboundAndDisabled()
    {
        var options = new AuditSelectedInvocationOptions();
        options.Enabled.Should().BeFalse();
        new AuditSelectedInvocationOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
        options.ToAuthority().Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteExpiredBindingCreatesImmutableAuthority(bool enabled)
    {
        var options = Valid();
        options.Enabled = enabled;
        new AuditSelectedInvocationOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
        var authority = options.ToAuthority();
        authority.Should().NotBeNull();
        authority.Enabled.Should().Be(enabled);
        authority.Binding.RunId.Should().Be(Guid.Parse(options.RunId!));
        authority.Binding.ExpectedRevision.Should().Be(long.MaxValue);
        authority.Binding.ScopeDigest.Should().Be(options.FullScopeDigest);
        authority.InvocationId.Should().Be(Guid.Parse(options.InvocationId!));
        authority.ExpiresAt.Should().Be(Instant.FromUtc(2020, 1, 2, 3, 4));
        options.RunId = Guid.NewGuid().ToString("D");
        options.ExpectedRevision = "0";
        options.FullScopeDigest = new string('b', 64);
        options.Enabled = !enabled;
        authority.Binding.ExpectedRevision.Should().Be(long.MaxValue);
        authority.Binding.ScopeDigest.Should().Be(new string('a', 64));
        authority.Enabled.Should().Be(enabled);
    }

    [Theory]
    [InlineData("RunId", "PRIVATE_RUN_SENTINEL")]
    [InlineData("RunId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("InvocationId", "PRIVATE_INVOCATION_SENTINEL")]
    [InlineData("FullScopeDigest", "PRIVATE_DIGEST_SENTINEL")]
    [InlineData("FullScopeDigest", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("ExpectedRevision", "PRIVATE_REVISION_SENTINEL")]
    [InlineData("ExpectedRevision", "9223372036854775808")]
    [InlineData("ExpectedRevision", "-1")]
    [InlineData("ExpectedRevision", "1.5")]
    [InlineData("ExpiresAtUtc", "PRIVATE_EXPIRY_SENTINEL")]
    [InlineData("ExpiresAtUtc", "2026-10-23T12:00:00")]
    [InlineData("ExpiresAtUtc", "2026-10-23T12:00:00+01:00")]
    public void ActualConfigurationBindingAndValidationDoNotEchoInvalidValues(string field, string value)
    {
        var config = Values();
        config[AuditSelectedInvocationOptions.SectionName + ":" + field] = value;
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build()
            .GetSection(AuditSelectedInvocationOptions.SectionName)
            .Get<AuditSelectedInvocationOptions>()!;
        var validation = new AuditSelectedInvocationOptionsValidator().Validate(null, options);
        validation.Failed.Should().BeTrue();
        validation.Failures.Should().ContainSingle().Which.Should().Be(field);
        var action = () => options.ToAuthority();
        action.Should().Throw<OptionsValidationException>().Which.Message.Should().NotContain(value);
    }

    [Theory]
    [InlineData("RunId")]
    [InlineData("InvocationId")]
    [InlineData("FullScopeDigest")]
    [InlineData("ExpectedRevision")]
    [InlineData("ExpiresAtUtc")]
    public void EachMissingBoundFieldIsRejected(string field)
    {
        var values = Values();
        values.Remove(AuditSelectedInvocationOptions.SectionName + ":" + field);
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build()
            .GetSection(AuditSelectedInvocationOptions.SectionName)
            .Get<AuditSelectedInvocationOptions>()!;
        new AuditSelectedInvocationOptionsValidator().Validate(null, options).Failed.Should().BeTrue();
    }

    [Fact]
    public void EnabledWithoutBindingIsInvalid()
    {
        new AuditSelectedInvocationOptionsValidator().Validate(null, new() { Enabled = true }).Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData("2020-01-02T03:04:00Z")]
    [InlineData("2020-01-02T03:04:00+00:00")]
    public void ExplicitUtcFormsAreAccepted(string expiry)
    {
        var options = Valid();
        options.ExpiresAtUtc = expiry;
        new AuditSelectedInvocationOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
        options.ToAuthority()!.ExpiresAt.Should().Be(Instant.FromUtc(2020, 1, 2, 3, 4));
    }

    private static AuditSelectedInvocationOptions Valid() =>
        new()
        {
            RunId = "4a0a3de8-572d-40b6-8d42-4b074b3ce080",
            InvocationId = "bf326bf0-0762-4372-b9f6-5b8de7e223d9",
            FullScopeDigest = new string('a', 64),
            ExpectedRevision = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ExpiresAtUtc = "2020-01-02T03:04:00Z",
        };

    private static Dictionary<string, string?> Values()
    {
        var options = Valid();
        var prefix = AuditSelectedInvocationOptions.SectionName + ":";
        return new()
        {
            [prefix + "RunId"] = options.RunId,
            [prefix + "InvocationId"] = options.InvocationId,
            [prefix + "FullScopeDigest"] = options.FullScopeDigest,
            [prefix + "ExpectedRevision"] = options.ExpectedRevision,
            [prefix + "ExpiresAtUtc"] = options.ExpiresAtUtc,
        };
    }

    [Fact]
    public void PartialBindingIsRejectedEvenWhenDisabled()
    {
        var options = new AuditSelectedInvocationOptions { RunId = Guid.NewGuid().ToString("D") };
        new AuditSelectedInvocationOptionsValidator().Validate(null, options).Failed.Should().BeTrue();
    }
}
