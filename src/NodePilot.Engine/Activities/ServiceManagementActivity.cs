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
///   create       — New-Service. Required: binaryPath. Optional: displayName, description, startupType.
///   delete       — sc.exe delete (after Stop-Service if running). Works on PS 5.1; Remove-Service is PS 6+.
///   setStartType — Set-Service -StartupType for Automatic|Manual|Disabled, sc.exe config for AutomaticDelayedStart.
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
        // we do not trust to be apostrophe-free.
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
        // patch the delayed-auto bit via sc.exe — same trick setStartType uses.
        if (string.Equals(startupType, "AutomaticDelayedStart", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" -StartupType Automatic");
            sb.Append("; & sc.exe config ").Append(qServiceName).Append(" start= delayed-auto | Out-Null");
        }
        else
        {
            sb.Append(" -StartupType ").Append(startupType);
        }

        return sb.ToString();
    }

    private static string BuildDeleteScript(string qServiceName)
    {
        // Stop the service first if present and running — sc.exe delete on a running service
        // marks it for deletion but only completes after stop. SilentlyContinue keeps a missing
        // service from raising before sc.exe gets to print its own error (which is what the
        // user wants to see in the Output panel).
        return
            $"if (Get-Service -Name {qServiceName} -ErrorAction SilentlyContinue) " +
            $"{{ Stop-Service -Name {qServiceName} -Force -ErrorAction SilentlyContinue }}; " +
            $"& sc.exe delete {qServiceName}";
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

        // AutomaticDelayedStart isn't supported by Set-Service on PS 5.1. Use sc.exe config:
        // the bit pattern is "start= auto" + a separate "delayed-auto" flag in newer schtasks
        // form, but `start= delayed-auto` is the documented one-shot since Vista.
        if (string.Equals(startupType, "AutomaticDelayedStart", StringComparison.OrdinalIgnoreCase))
        {
            return $"& sc.exe config {qServiceName} start= delayed-auto | Out-Null; " +
                   $"& sc.exe qc {qServiceName} | Select-String 'START_TYPE'";
        }

        return $"Set-Service -Name {qServiceName} -StartupType {startupType}";
    }

    protected override ActivityResult PostProcess(ActivityResult raw, JsonElement config)
    {
        // Default mirrors BuildScript: when action is omitted we run `status`, so PostProcess
        // must default the same way — otherwise OutputParameters stays empty and downstream
        // edges comparing param.status get "" and fail their == checks.
        var action = (config.GetStringOrNull("action") ?? "status").ToLowerInvariant();
        if (action != "status" || !raw.Success || string.IsNullOrWhiteSpace(raw.Output))
            return raw;

        var op = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(raw.Output!);
            var root = doc.RootElement;
            // ConvertTo-Json wraps single-result objects in {} but multi-result in []. Get-Service
            // for a single service is single-result, but we defensively unwrap a 1-element array too.
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1)
                root = root[0];

            if (root.ValueKind == JsonValueKind.Object)
            {
                CopyStringField(root, "Name", op, "name");
                CopyStringField(root, "Status", op, "status");
                CopyStringField(root, "StartType", op, "startType");
            }
        }
        catch (JsonException)
        {
            // Output wasn't JSON (e.g. because the service was missing and PowerShell printed an
            // error instead) — we leave Success/Output unchanged and return empty
            // OutputParameters; the caller sees ErrorOutput anyway.
        }

        if (op.Count == 0) return raw;

        return new ActivityResult
        {
            Success = raw.Success,
            Output = raw.Output,
            ErrorOutput = raw.ErrorOutput,
            Duration = raw.Duration,
            OutputParameters = op,
        };
    }
}
