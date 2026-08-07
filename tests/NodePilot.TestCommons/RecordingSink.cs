using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.TestCommons;

/// <summary>
/// Recording <see cref="INotificationSink"/> for alerting tests: captures every send
/// (context + target + secret) and answers with a programmable
/// <see cref="NotificationSendResult"/>. Lives here because the sink contract is defined in
/// NodePilot.Core and both the dispatcher tests (Engine.Tests) and the alerting controller
/// tests (Api.Tests) previously carried near-identical private copies — this type is the
/// union of those six copies.
/// </summary>
public sealed class RecordingSink(NotificationChannel channel = NotificationChannel.Email) : INotificationSink
{
    public NotificationChannel Channel { get; } = channel;

    /// <summary>Every delivered send, in order. Tuple names are lowercase on purpose so the
    /// pre-consolidation call sites (<c>Sends[0].ctx</c>, <c>.Which.target</c>) keep compiling.</summary>
    public List<(NotificationContext ctx, string target, string? secret)> Sends { get; } = [];

    /// <summary>Optional per-send verdict; null means every send reports success.</summary>
    public Func<NotificationSendResult>? Behavior { get; set; }

    /// <summary>Hook invoked before the send is recorded — used to inject mid-delivery races.</summary>
    public Action? BeforeSend { get; set; }

    public Task<NotificationSendResult> SendAsync(NotificationContext ctx, string target, string? secret, CancellationToken ct)
    {
        BeforeSend?.Invoke();
        Sends.Add((ctx, target, secret));
        return Task.FromResult(Behavior?.Invoke() ?? NotificationSendResult.Ok);
    }
}
