using AiObservatory.Api.Endpoints;
using AwesomeAssertions;

namespace AiObservatory.Api.Tests.Services;

public class NotificationMaskingTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("ch@fixportal.org", "ch***@fixportal.org")]
    [InlineData("c@fixportal.org", "c***@fixportal.org")]
    [InlineData("christopher@fixportal.org", "ch***@fixportal.org")]
    public void MaskEmail_shows_at_most_the_first_two_local_part_characters(string? input, string? expected)
    {
        NotificationMasking.MaskEmail(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("https://hooks.slack.com/services/T0/B0/verysecret", "https://hooks.slack.com/services/***")]
    public void MaskWebhookUrl_never_reveals_the_real_path(string? input, string? expected)
    {
        NotificationMasking.MaskWebhookUrl(input).Should().Be(expected);
    }
}
