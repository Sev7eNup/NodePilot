using System.Net;
using System.Net.Mail;
using System.Text.Json;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Execution;
using NodePilot.Engine.Options;
using Microsoft.Extensions.Options;
using NodePilot.Engine.Mail;

namespace NodePilot.Engine.Activities;

public class EmailActivity : IActivityExecutor
{
    private readonly IOptionsMonitor<SmtpOptions> _smtp;

    public string ActivityType => "emailNotification";

    public EmailActivity(IOptionsMonitor<SmtpOptions> smtp)
    {
        _smtp = smtp;
    }

    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
        => ActivityExecution.RunAsync(async () =>
        {
            var to = config.GetStringOrNull("to");
            if (string.IsNullOrWhiteSpace(to))
                return new ActivityResult { Success = false, ErrorOutput = "Email: 'to' is required" };

            // Reject comma/semicolon-separated recipient lists. An Operator (or trigger payload
            // injected via {{...}}) could otherwise BCC attackers onto workflow notifications and
            // exfiltrate log contents. Single-recipient only — build a second step for fan-out.
            if (SmtpTransport.IsRecipientList(to))
                return new ActivityResult { Success = false, ErrorOutput = "Email: 'to' must be a single recipient (no comma/semicolon lists)" };

            var subject = config.GetString("subject", "");
            var body = config.GetString("body", "");

            // Header-injection defense: CR/LF in address or subject would split headers. .NET's
            // MailMessage already rejects these in most paths, but we fail early with a clear
            // error.
            if (SmtpTransport.HasHeaderInjection(to, subject))
                return new ActivityResult { Success = false, ErrorOutput = "Email: newline characters are not allowed in 'to' or 'subject'" };

            // TLS is on by default. SmtpClient itself defaults to EnableSsl=false, which would
            // send LOGIN/PLAIN credentials and the whole message body in plaintext. SmtpOptions
            // sets a safe default, and SecurityHardeningWarnings warns at boot if TLS is off
            // while a Username is still configured.
            //
            // SmtpOptions is read per execution so a live config edit takes effect without a
            // service restart.
            var o = _smtp.CurrentValue;
            using var smtpClient = SmtpTransport.CreateClient(o);

            var message = new MailMessage(o.From, to, subject, body);

            // Accepts boolean true or the string "true", matching every other boolean config
            // knob in the engine — a value can arrive as a string after template resolution,
            // e.g. {{manual.isHtml}}.
            if (config.GetBool("isHtml", false))
                message.IsBodyHtml = true;

            // Honors `timeoutSeconds` from the activity config. Default 30s, mirroring
            // SmtpClient's own default.
            //
            // System.Net.Mail.SmtpClient's own cancellation is unreliable: a token that trips
            // mid-connect (a dev SMTP server black-holing the SYN, or a slow relay) can leave
            // the returned Task unresolved, parking the engine scheduler's Task.WhenAny forever
            // and stranding the whole execution in Running. Bounding the await with WaitAsync
            // makes the step always resolve within the timeout — TimeoutException becomes a
            // failed step, run-cancel becomes Cancelled — regardless of SmtpClient's internal
            // state. The abandoned send task is torn down when `smtpClient` is disposed.
            var timeoutSeconds = config.GetOptionalPositiveInt("timeoutSeconds") ?? DefaultSmtpTimeoutSeconds;
            try
            {
                await smtpClient.SendMailAsync(message).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct);
            }
            catch (TimeoutException)
            {
                return new ActivityResult { Success = false, ErrorOutput = $"Email: send timed out after {timeoutSeconds}s" };
            }
            catch (SmtpException ex)
            {
                // SmtpException.Message is almost always the constant "Failure sending mail.",
                // which does not distinguish a refused connection, a TLS mismatch, or a
                // rejected recipient. Report the endpoint used and the underlying transport
                // error instead.
                return new ActivityResult
                {
                    Success = false,
                    ErrorOutput = DescribeSmtpFailure(ex, o),
                };
            }

            return new ActivityResult { Success = true, Output = $"Email sent to {to}" };
        });

    /// <summary>
    /// Builds an actionable SMTP error: the endpoint actually used, the TLS mode, the SMTP
    /// status code when the server supplied one, and the flattened inner-exception chain
    /// (which carries the real cause, e.g. the SocketException for a refused connection).
    /// </summary>
    internal static string DescribeSmtpFailure(SmtpException ex, SmtpOptions options)
    {
        // StatusCode is GeneralFailure when the failure happened below the SMTP protocol
        // (DNS, TCP, TLS) — naming it then would suggest a server response that never existed.
        var status = ex.StatusCode == SmtpStatusCode.GeneralFailure
            ? null
            : $", status {ex.StatusCode}";
        var cause = ExceptionDetail.Describe(ex.InnerException);
        if (string.IsNullOrEmpty(cause)) cause = ex.Message;

        return $"Email: SMTP send via {options.Host}:{options.Port} "
               + $"(TLS={(options.EnableSsl ? "on" : "off")}{status}) failed: {cause}";
    }

    // Bounds the SMTP send so a stuck connection cannot pin a step indefinitely. Override
    // per-activity via the `timeoutSeconds` config field (positive integer).
    internal const int DefaultSmtpTimeoutSeconds = 30;
}
