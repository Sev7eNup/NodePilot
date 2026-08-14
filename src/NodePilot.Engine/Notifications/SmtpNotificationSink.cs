using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Options;
using NodePilot.Engine.Mail;

namespace NodePilot.Engine.Notifications;

/// <summary>
/// E-mail delivery sink. Reuses the configured <see cref="SmtpOptions"/> + the same hardening as
/// <see cref="Activities.EmailActivity"/> (default-on TLS, single recipient, header-injection guard,
/// bounded send timeout). Self-isolating: any failure is returned as a failed result, never thrown,
/// so one bad route can't break the dispatch pass.
/// </summary>
public sealed class SmtpNotificationSink : INotificationSink
{
    private const int SendTimeoutSeconds = 30;
    private readonly IOptionsMonitor<SmtpOptions> _smtp;

    public SmtpNotificationSink(IOptionsMonitor<SmtpOptions> smtp) => _smtp = smtp;

    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<NotificationSendResult> SendAsync(NotificationContext ctx, string target, string? secret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target))
            return NotificationSendResult.Fail("Email route has no recipient.");
        if (SmtpTransport.IsRecipientList(target))
            return NotificationSendResult.Fail("Email route target must be a single recipient (no comma/semicolon lists).");

        var subject = NotificationRenderer.Title(ctx);
        if (SmtpTransport.HasHeaderInjection(target, subject))
            return NotificationSendResult.Fail("Email: newline characters are not allowed in recipient or subject.");

        try
        {
            // Hot-reload: read SmtpOptions per send so a live config edit (appsettings.runtime.json
            // or Admin-Settings-UI save) takes effect without a service restart. The sink is a
            // singleton — IOptionsMonitor is the correct live source (IOptionsSnapshot would throw).
            var o = _smtp.CurrentValue;
            using var client = SmtpTransport.CreateClient(o);
            using var message = new MailMessage(o.From, target, subject, NotificationRenderer.EmailBody(ctx));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(SendTimeoutSeconds));
            await client.SendMailAsync(message, cts.Token);
            return NotificationSendResult.Ok;
        }
        catch (Exception ex)
        {
            return NotificationSendResult.Fail(ex.Message);
        }
    }
}
