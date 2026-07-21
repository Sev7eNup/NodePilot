using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Notifications;

/// <summary>
/// Generic outbound-webhook sink: POSTs the rendered JSON to the route URL through the SSRF-guarded
/// "NodePilot" named HttpClient (URL validated up front by <see cref="NetworkGuard.ValidateUrl"/> and
/// again at TCP-connect time inside the handler). If the route carries a secret, the body is signed
/// with HMAC-SHA256 in an <c>X-NodePilot-Signature: sha256=…</c> header. Self-isolating with a bounded
/// timeout. (Teams/Slack are expected to reuse this sink with their own renderers in a
/// later alerting release.)
/// </summary>
public sealed class WebhookNotificationSink : INotificationSink
{
    private const int SendTimeoutSeconds = 15;
    private readonly RestApiHttpClientProvider _clients;
    private readonly IConfiguration _config;

    public WebhookNotificationSink(RestApiHttpClientProvider clients, IConfiguration config)
    {
        _clients = clients;
        _config = config;
    }

    public NotificationChannel Channel => NotificationChannel.GenericWebhook;

    public async Task<NotificationSendResult> SendAsync(NotificationContext ctx, string target, string? secret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target))
            return NotificationSendResult.Fail("Webhook route has no URL.");

        try { NetworkGuard.ValidateUrl(_config, target); }
        catch (Exception ex) { return NotificationSendResult.Fail($"Blocked webhook URL: {ex.Message}"); }

        var body = NotificationRenderer.WebhookJson(ctx);
        try
        {
            var client = _clients.GetClient(default); // named "NodePilot" client (SSRF-guarded connect)
            using var req = new HttpRequestMessage(HttpMethod.Post, target)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(secret))
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
                req.Headers.TryAddWithoutValidation("X-NodePilot-Signature", $"sha256={sig}");
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(SendTimeoutSeconds));
            using var resp = await client.SendAsync(req, cts.Token);
            return resp.IsSuccessStatusCode
                ? NotificationSendResult.Ok
                : NotificationSendResult.Fail($"Webhook returned HTTP {(int)resp.StatusCode}.");
        }
        catch (Exception ex)
        {
            return NotificationSendResult.Fail(ex.Message);
        }
    }
}
