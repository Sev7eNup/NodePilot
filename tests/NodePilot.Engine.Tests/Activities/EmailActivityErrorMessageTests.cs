using System.Net.Mail;
using System.Net.Sockets;
using FluentAssertions;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Options;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// <see cref="SmtpException.Message"/> is almost always the constant "Failure sending mail."
/// A lab run on 2026-08-02 produced 2021 failures that were indistinguishable from each
/// other: a refused connection, a TLS mismatch and a rejected recipient all rendered the
/// same seven words. These tests pin the replacement message down to the parts that let an
/// operator act — endpoint, TLS mode, and the transport-level cause.
/// </summary>
public class EmailActivityErrorMessageTests
{
    private static SmtpOptions Options(string host = "smtp.contoso.example", int port = 25, bool ssl = true)
        => new() { Host = host, Port = port, EnableSsl = ssl, From = "nodepilot@contoso.example" };

    [Fact]
    public void DescribeSmtpFailure_RefusedConnection_NamesEndpointAndUnderlyingCause()
    {
        var ex = new SmtpException(
            SmtpStatusCode.GeneralFailure,
            "Failure sending mail.")
        {
            // The SocketException is where the real diagnosis lives.
        };
        var withInner = new SmtpException("Failure sending mail.",
            new SocketException((int)SocketError.ConnectionRefused));

        var message = EmailActivity.DescribeSmtpFailure(withInner, Options("localhost", 2525, ssl: false));

        message.Should().StartWith("Email: SMTP send via localhost:2525");
        message.Should().Contain("TLS=off");
        message.Should().NotBe("Failure sending mail.");
        message.Should().NotContain("Failure sending mail.",
            "the inner cause replaces the useless constant, it does not accompany it");
        _ = ex;
    }

    [Fact]
    public void DescribeSmtpFailure_ReportsTlsModeOn()
    {
        var ex = new SmtpException("Failure sending mail.", new SocketException((int)SocketError.HostNotFound));

        EmailActivity.DescribeSmtpFailure(ex, Options(ssl: true)).Should().Contain("TLS=on");
    }

    [Fact]
    public void DescribeSmtpFailure_ServerSuppliedStatus_IsNamed()
    {
        var ex = new SmtpException(SmtpStatusCode.MailboxUnavailable, "Failure sending mail.");

        var message = EmailActivity.DescribeSmtpFailure(ex, Options());

        message.Should().Contain("status MailboxUnavailable");
    }

    [Fact]
    public void DescribeSmtpFailure_GeneralFailure_DoesNotInventAServerStatus()
    {
        // GeneralFailure means the failure happened below SMTP (DNS/TCP/TLS). Naming it
        // would imply a server response that never existed.
        var ex = new SmtpException(SmtpStatusCode.GeneralFailure, "Failure sending mail.");

        EmailActivity.DescribeSmtpFailure(ex, Options()).Should().NotContain("status");
    }

    [Fact]
    public void DescribeSmtpFailure_NoInnerException_StillReportsEndpoint()
    {
        var ex = new SmtpException(SmtpStatusCode.GeneralFailure, "Failure sending mail.");

        var message = EmailActivity.DescribeSmtpFailure(ex, Options("relay.contoso.example", 587));

        message.Should().Contain("relay.contoso.example:587");
        // Without an inner exception the SMTP message is all we have — keep it rather than
        // reporting nothing.
        message.Should().EndWith("Failure sending mail.");
    }
}
