using System.Diagnostics.Metrics;
using NodePilot.Core.Telemetry;

namespace NodePilot.Remote;

/// <summary>
/// Remote-layer metrics. The meter name comes from the shared Core constants registry —
/// no NodePilot.Telemetry dependency needed, and no "keep in sync" literal either.
/// </summary>
public static class RemoteMetrics
{
    public static readonly Meter Meter = new(TelemetryConstants.Meters.Remote, "1.0.0");

    public static readonly Counter<long> SessionsOpened = Meter.CreateCounter<long>(
        "nodepilot.winrm.sessions.opened", unit: "1", description: "WinRM sessions that were opened (tagged by result and auth type).");

    public static readonly Histogram<double> SessionOpenDuration = Meter.CreateHistogram<double>(
        "nodepilot.winrm.session.open.duration", unit: "ms", description: "Time spent opening a WinRM runspace.");

    public static readonly UpDownCounter<long> SessionsActive = Meter.CreateUpDownCounter<long>(
        "nodepilot.winrm.sessions.active", unit: "1", description: "Currently open WinRM runspaces.");

    public static readonly Histogram<double> ScriptDuration = Meter.CreateHistogram<double>(
        "nodepilot.winrm.script.duration", unit: "ms", description: "Per-script execution duration on an established WinRM session.");

    public static readonly Counter<long> ScriptTimeouts = Meter.CreateCounter<long>(
        "nodepilot.winrm.script.timeouts", unit: "1", description: "WinRM script invocations that hit their timeout.");

    public static readonly Counter<long> AuthFailures = Meter.CreateCounter<long>(
        "nodepilot.winrm.auth.failures", unit: "1", description: "WinRM connect attempts that failed (likely auth / connectivity).");
}
