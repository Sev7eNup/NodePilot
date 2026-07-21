using System.Net;
using System.Net.Mail;
using System.Text.Json;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Options;
using Microsoft.Extensions.Options;

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
            if (to.IndexOfAny(new[] { ',', ';' }) >= 0)
                return new ActivityResult { Success = false, ErrorOutput = "Email: 'to' must be a single recipient (no comma/semicolon lists)" };

            var subject = config.GetString("subject", "");
            var body = config.GetString("body", "");

            // Header-injection defense: CR/LF in address or subject would split headers. .NET's
            // MailMessage already rejects these in most paths, but we fail early with a clear error.
            if (to.IndexOfAny(new[] { '\r', '\n' }) >= 0 || subject.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                return new ActivityResult { Success = false, ErrorOutput = "Email: newline characters are not allowed in 'to' or 'subject'" };

            // H-2 (security audit 2026-05-15): default-on TLS. SmtpClient defaults to
            // EnableSsl=false; that would send LOGIN/PLAIN credentials + the whole message
            // body in plaintext. The option now lives on SmtpOptions with a safe default,
            // and SecurityHardeningWarnings yells at boot if an operator flipped it off
            // while still configuring a Username.
            //
            // Hot-reload: read SmtpOptions per execution so a live config edit takes effect
            // without a service restart.
            var o = _smtp.CurrentValue;
            using var smtpClient = new SmtpClient(o.Host, o.Port)
            {
                EnableSsl = o.EnableSsl,
            };

            if (o.Username is not null && o.Password is not null)
                smtpClient.Credentials = new NetworkCredential(o.Username, o.Password);

            var message = new MailMessage(o.From, to, subject, body);

            // D5: accept `true`-literal AND `"true"`-string, matching every other
            // boolean knob in the engine. The previous `isHtml.GetBoolean()` call
            // threw InvalidOperationException when the value came in as a string
            // (which happens after template resolution of e.g. {{manual.isHtml}}).
            if (config.GetBool("isHtml", false))
                message.IsBodyHtml = true;

            // D6: honour `timeoutSeconds` from the activity config. Default 30s
            // (mirrors SmtpClient's default).
            //
            // System.Net.Mail.SmtpClient's own cancellation is RACY: a token that trips
            // mid-connect (dev SMTP black-holing the SYN, or a slow relay) can leave the
            // returned Task unresolved. An unresolved step task parks the engine scheduler
            // inside `Task.WhenAny` forever, so the whole execution strands in `Running`.
            // Bound the AWAIT itself with WaitAsync so the step is GUARANTEED to resolve
            // within the timeout — TimeoutException -> failed step, run-cancel -> Cancelled —
            // regardless of SmtpClient's internal state. The abandoned send task is torn down
            // when `smtpClient` is disposed on the way out.
            var timeoutSeconds = config.GetOptionalPositiveInt("timeoutSeconds") ?? DefaultSmtpTimeoutSeconds;
            try
            {
                await smtpClient.SendMailAsync(message).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct);
            }
            catch (TimeoutException)
            {
                return new ActivityResult { Success = false, ErrorOutput = $"Email: send timed out after {timeoutSeconds}s" };
            }

            return new ActivityResult { Success = true, Output = $"Email sent to {to}" };
        });

    // D6: bounded SMTP timeout so a stuck connection cannot pin a step indefinitely.
    // Override per-activity via the `timeoutSeconds` config field (positive integer).
    internal const int DefaultSmtpTimeoutSeconds = 30;
}
