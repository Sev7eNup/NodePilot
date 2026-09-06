using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Manages Windows services on a remote machine. Lifecycle (create/delete) plus runtime
/// control (start/stop/restart) plus configuration (StartupType) — covers the SCOrch
/// "Manage Service" activity surface in one node.
///
/// Actions:
///   start        — Start-Service.
///   stop         — Stop-Service -Force.
///   restart      — Restart-Service -Force.
///   status       — Get-Service projected to Name/Status/StartType (human-readable).
///   create       - New-Service with binaryPath and optional metadata
/// delete — sc.exe delete (after Stop-Service if running). Works on PS 5.1; Remove-Service is PS
/// 6+.
/// setStartType — Set-Service -StartupType for Automatic|Manual|Disabled, sc.exe config for
/// AutomaticDelayedStart.
///
/// Common config:
///   serviceName  string, required — service short name (not display name).
///
/// Create-only config:
///   binaryPath   string, required — fully-qualified path to the service executable.
///   displayName  string, optional — friendly name shown in services.msc.
///   description  string, optional.
///   startupType  string, optional — "Automatic" | "Manual" | "Disabled" | "AutomaticDelayedStart".
///                                   Default "Automatic" for create. Required for setStartType.
/// </summary>
public class ServiceManagementActivity : BaseRemoteActivity
{
    private static readonly HashSet<string> KnownActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "start", "stop", "restart", "status", "create", "delete", "setstarttype",
    };

    // Set-Service (PS 5.1) accepts Automatic/Manual/Disabled/Boot/System but NOT
    // AutomaticDelayedStart — that came in PS 6.1+. We expose the four values that map
    // cleanly onto user expectations and fall back to sc.exe for delayed-auto.
    private static readonly HashSet<string> KnownStartupTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Automatic", "Manual", "Disabled", "AutomaticDelayedStart",
    };

    public override string ActivityType => "serviceManagement";

    public ServiceManagementActivity(
        IRemoteSessionFactory sessionFactory,
        ICredentialStore credentialStore,
        NodePilot.Data.NodePilotDbContext db,
        PowerShellEngineFactory engineFactory,
        IConfiguration configuration)
        : base(sessionFactory, credentialStore, db, engineFactory, configuration) { }

    protected override string BuildScript(JsonElement config, StepExecutionContext context)
    {
        var serviceName = config.GetStringOrNull("serviceName");
        if (string.IsNullOrWhiteSpace(serviceName))
            throw new InvalidOperationException("Service Management: 'serviceName' is required");

        var action = (config.GetStringOrNull("action") ?? "status").ToLowerInvariant();
        if (!KnownActions.Contains(action))
            throw new InvalidOperationException($"Unknown service action: {action}");

        // Always route through PowerShellQuoter — serviceName may originate from an upstream
        // step's output (via {{step.param.X}} resolution), i.e. data from another machine that
        // is not trusted to be apostrophe-free.
        var q = PowerShellQuoter.Literal(serviceName);

        return action switch
        {
            "start" => $"Start-Service -Name {q}",
            "stop" => $"Stop-Service -Name {q} -Force",
            "restart" => $"Restart-Service -Name {q} -Force",
            "status" => $"Get-Service -Name {q} | Select-Object Name, @{{N='Status';E={{$_.Status.ToString()}}}}, @{{N='StartType';E={{$_.StartType.ToString()}}}} | ConvertTo-Json -Compress",
            "create" => BuildCreateScript(config, q),
            "delete" => BuildDeleteScript(q),
            "setstarttype" => BuildSetStartTypeScript(config, q),
            _ => throw new InvalidOperationException($"Unknown service action: {action}")
        };
    }

    private static string BuildCreateScript(JsonElement config, string qServiceName)
    {
        var binaryPath = config.GetStringOrNull("binaryPath");
        if (string.IsNullOrWhiteSpace(binaryPath))
            throw new InvalidOperationException("Service Management (create): 'binaryPath' is required");

        var startupType = config.GetString("startupType", "Automatic");
        if (!KnownStartupTypes.Contains(startupType))
            throw new InvalidOperationException(
                $"Service Management (create): unknown startupType '{startupType}'. " +
                $"Allowed: {string.Join(", ", KnownStartupTypes)}");

        var displayName = config.GetStringOrNull("displayName");
        var description = config.GetStringOrNull("description");

        var sb = new StringBuilder();
        sb.Append("New-Service -Name ").Append(qServiceName)
          .Append(" -BinaryPathName ").Append(PowerShellQuoter.Literal(binaryPath));
        if (!string.IsNullOrWhiteSpace(displayName))
            sb.Append(" -DisplayName ").Append(PowerShellQuoter.Literal(displayName));
        if (!string.IsNullOrWhiteSpace(description))
            sb.Append(" -Description ").Append(PowerShellQuoter.Literal(description));

        // New-Service in PS 5.1 doesn't know AutomaticDelayedStart. Create with Automatic, then
        // patch the delayed-auto bit via sc.exe — the same approach setStartType uses.
        if (string.Equals(startupType, "AutomaticDelayedStart", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" -StartupType Automatic");
            sb.Append("; ").Append(ScGuarded("config", $"config {qServiceName} start= delayed-auto", emitOutput: false));
        }
        else
        {
            sb.Append(" -StartupType ").Append(startupType);
        }

        return sb.ToString();
    }

    private static string BuildDeleteScript(string qServiceName)
    {
        // Stop the service first if present and running: sc.exe delete on a running service
        // marks it for deletion but completes only after it stops. SilentlyContinue prevents a
        // missing service from raising before sc.exe prints its own, more specific error.
        return
            $"if (Get-Service -Name {qServiceName} -ErrorAction SilentlyContinue) " +
            $"{{ Stop-Service -Name {qServiceName} -Force -ErrorAction SilentlyContinue }}; " +
            ScGuarded("delete", $"delete {qServiceName}", emitOutput: true);
    }

    /// <summary>
    /// Emits an sc.exe invocation whose non-zero exit becomes a terminating PowerShell error.
    /// sc.exe writes its failures to stdout and leaves the error stream empty, so a bare call
    /// keeps <c>HadErrors</c> false and a failed delete/config would report the step as
    /// succeeded. <paramref name="emitOutput"/> re-emits the captured text so it still reaches
    /// <c>{{step.output}}</c>.
    /// </summary>
    private static string ScGuarded(string verb, string arguments, bool emitOutput)
    {
        var sb = new StringBuilder();
        sb.Append("$__npSc = & sc.exe ").Append(arguments).Append("; ");
        sb.Append("if ($LASTEXITCODE -ne 0) { throw \"sc.exe ").Append(verb)
          .Append(" failed with exit code $LASTEXITCODE: $($__npSc -join ' ')\" }");
        if (emitOutput)
            sb.Append("; $__npSc");
        return sb.ToString();
    }

    private static string BuildSetStartTypeScript(JsonElement config, string qServiceName)
    {
        var startupType = config.GetStringOrNull("startupType");
        if (string.IsNullOrWhiteSpace(startupType))
            throw new InvalidOperationException("Service Management (setStartType): 'startupType' is required");
        if (!KnownStartupTypes.Contains(startupType))
            throw new InvalidOperationException(
                $"Service Management (setStartType): unknown startupType '{startupType}'. " +
                $"Allowed: {string.Join(", ", KnownStartupTypes)}");

        // Set-Service on PS 5.1 does not support AutomaticDelayedStart. Use sc.exe config
        // instead — `start= delayed-auto` sets the delayed-auto flag directly.
        if (string.Equals(startupType, "AutomaticDelayedStart", StringComparison.OrdinalIgnoreCase))
        {
            return ScGuarded("config", $"config {qServiceName} start= delayed-auto", emitOutput: false) + "; " +
                   $"& sc.exe qc {qServiceName} | Select-String 'START_TYPE'";
        }

        return $"Set-Service -Name {qServiceName} -StartupType {startupType}";
    }

    protected override ActivityResult PostProcess(ActivityResult raw, JsonElement config)
    {
        // Mirrors BuildScript: when action is omitted, the default is `status`. PostProcess
        // must use the same default, or OutputParameters stays empty and downstream edges
        // comparing param.status get "" and fail their == checks.
        var action = (config.GetStringOrNull("action") ?? "status").ToLowerInvariant();
        if (action != "status" || !raw.Success || string.IsNullOrWhiteSpace(raw.Output))
            return raw;

        // Non-JSON output (e.g. because the service was missing and PowerShell printed an error
        // instead) projects to nothing — Success/Output stay unchanged and the caller sees
        // ErrorOutput anyway.
        var op = PowerShellOperation.MapStatusJsonFields(
            raw.Output,
            ("Name", "name"),
            ("Status", "status"),
            ("StartType", "startType"));

        return WithOutputParameters(raw, op);
    }
}
