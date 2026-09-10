using AwesomeAssertions;

namespace AiObservatory.Api.Tests;

/// <summary>
/// S4: the startup seed for the legacy BUDGET_ALERT_EMAIL_TO env var must pass the same
/// validation the notification-settings endpoint applies — an invalid stored recipient throws
/// in EmailAlertNotifier at send time and wedges the alert claim into an endless retry loop.
/// </summary>
public class ProgramLegacyAlertEmailSeedTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("a@x.com, b@y.com")]
    [InlineData("@nodomain")]
    [InlineData("a@")]
    [InlineData("@Microsoft.KeyVault(VaultName=v;SecretName=s)")]
    public void RejectsAnUnsetOrInvalidLegacyRecipient(string? value)
    {
        Program.IsSeedableLegacyAlertEmail(value).Should().BeFalse();
    }

    [Theory]
    [InlineData("ops@fixportal.com")]
    [InlineData("Chris Dowling <chris@fixportal.com>")]
    public void AcceptsAValidLegacyRecipient(string value)
    {
        Program.IsSeedableLegacyAlertEmail(value).Should().BeTrue();
    }
}
