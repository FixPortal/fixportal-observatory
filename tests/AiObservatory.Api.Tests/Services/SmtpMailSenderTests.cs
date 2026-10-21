using AiObservatory.Api.Services;
using AwesomeAssertions;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AiObservatory.Api.Tests.Services;

public class SmtpMailSenderTests
{
    private static readonly IConfiguration Config = new ConfigurationBuilder().Build();

    [Fact]
    public async Task SendAsync_WhenTheSendFails_StillDisconnectsAnOpenConnection()
    {
        // The disconnect lives in a finally: a failed send must not leak a live SMTP session.
        var smtp = Substitute.For<ISmtpClient>();
        smtp.IsConnected.Returns(true);
        smtp.SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>(), Arg.Any<ITransferProgress>())
            .ThrowsAsync(new InvalidOperationException("send failed"));
        var sut = new SmtpMailSender(smtp, Config);

        var act = () => sut.SendAsync(new MimeMessage(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("send failed");
        await smtp.Received(1).DisconnectAsync(true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WhenTheConnectFails_DoesNotDisconnectAConnectionThatNeverOpened()
    {
        var smtp = Substitute.For<ISmtpClient>();
        smtp.IsConnected.Returns(false);
        smtp.ConnectAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<SecureSocketOptions>(),
                Arg.Any<CancellationToken>()
            )
            .ThrowsAsync(new InvalidOperationException("connect failed"));
        var sut = new SmtpMailSender(smtp, Config);

        var act = () => sut.SendAsync(new MimeMessage(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("connect failed");
        await smtp.DidNotReceive().DisconnectAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
