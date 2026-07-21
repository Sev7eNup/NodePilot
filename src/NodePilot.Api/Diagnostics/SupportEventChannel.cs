using System.Threading.Channels;
using NodePilot.Core.Models;

namespace NodePilot.Api.Diagnostics;

/// <summary>
/// Singleton channel between the Serilog sub-sink (<see cref="SupportEventDbSink"/>) and
/// the background flush service (<see cref="SupportEventFlushService"/>).
///
/// <para><b>Drop-newest backpressure:</b> bounded at 1024 events, FullMode=DropWrite.
/// When the channel is full (DB unreachable, flush loop running slow), the newest write is
/// dropped and <see cref="NodePilot.Engine.EngineMetrics.SupportEventsDropped"/> is incremented
/// with tag <c>reason=channel_full</c>. The Serilog hot path never blocks.</para>
///
/// <para>Note: the channel size is hardcoded for now. If real traffic ever exceeds it, a
/// config knob (<c>Logging:SupportLog:ChannelCapacity</c>) can be added later — recreating
/// the channel at boot is trivial.</para>
/// </summary>
public sealed class SupportEventChannel
{
    private readonly Channel<SupportEvent> _channel;

    public SupportEventChannel()
    {
        _channel = Channel.CreateBounded<SupportEvent>(new BoundedChannelOptions(capacity: 1024)
        {
            // DropWrite: when the channel is full, the write attempt that's currently coming
            // in gets discarded — the channel's existing contents stay untouched and the
            // reader carries on as before. That's exactly what we want: events already queued
            // are safe from the drop, new ones lose out to keep the workflow hot path unblocked.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// Non-blocking write. Returns <c>true</c> if the event was accepted, <c>false</c>
    /// if the channel was full (the caller should increment the drop counter in that case).
    /// </summary>
    public bool TryWrite(SupportEvent ev) => _channel.Writer.TryWrite(ev);

    /// <summary>Reader for the flush service. Iterates until Channel.Complete() is called.</summary>
    public ChannelReader<SupportEvent> Reader => _channel.Reader;
}
