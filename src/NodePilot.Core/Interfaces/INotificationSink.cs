using NodePilot.Core.Enums;
using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

/// <summary>
/// A delivery channel implementation (e-mail, generic webhook, Teams, …). One sink per
/// <see cref="NotificationChannel"/>; the dispatcher picks the sink whose <see cref="Channel"/>
/// matches the route. Implementations live in NodePilot.Engine (they need SMTP / the SSRF-guarded
/// HttpClient). They MUST be self-isolating: a slow/failing endpoint returns a failed
/// <see cref="NotificationSendResult"/> within a bounded timeout rather than throwing.
/// </summary>
public interface INotificationSink
{
    NotificationChannel Channel { get; }

    /// <param name="ctx">The occurrence to render + send.</param>
    /// <param name="target">The route's destination (recipient / URL / key).</param>
    /// <param name="secret">Decrypted route secret (HMAC key etc.), or null.</param>
    Task<NotificationSendResult> SendAsync(NotificationContext ctx, string target, string? secret, CancellationToken ct);
}
