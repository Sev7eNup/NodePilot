using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Remote power-management activity — shutdown / restart / logoff / abort / hibernate,
/// the equivalent of SCOrch's "Restart System". Uses <c>shutdown.exe</c> on the target
/// machine (or locally via the localhost bypass).
///
/// Config:
///   action        string, required — "shutdown" | "restart" | "logoff" | "abort" | "hibernate"
///   delaySeconds  int, default 0   — /t N countdown (shutdown/restart only; positive values require force)
///   force         bool, default true — /f, closes running apps without prompting
///   message       optional shutdown or restart dialog comment
///
/// A "restart" targeting the host that NodePilot itself runs on terminates the API
/// process and every execution running on it. A delay &gt; 0 gives the step time to return
/// cleanly before the OS shuts down.
/// </summary>
public class PowerManagementActivity : BaseRemoteActivity
{
    public override string ActivityType => "powerManagement";

    // Actions that turn the target host off / reboot it. `abort` and `logoff` do not
    // interrupt the running engine process even on a self-target, so they're excluded
    // from the self-shutdown guard.
    private static readonly HashSet<string> DestructiveSelfActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "shutdown", "restart", "hibernate",
    };

    public PowerManagementActivity(
        IRemoteSessionFactory sessionFactory,
        ICredentialStore credentialStore,
        NodePilot.Data.NodePilotDbContext db,
        PowerShellEngineFactory engineFactory,
        IConfiguration configuration)
        : base(sessionFactory, credentialStore, db, engineFactory, configuration) { }

    /// <summary>
    /// Refuses to shut down, restart or hibernate the NodePilot host itself unless an admin opts
    /// in: the localhost bypass would otherwise let a careless workflow take the engine offline
    /// with every in-flight execution. Judged on the same resolved target the step runs on.
    /// </summary>
    private protected override void ValidateTarget(JsonElement config, ActivityTarget target)
    {
        var action = config.GetStringOrNull("action");
        if (action is not null
            && DestructiveSelfActions.Contains(action)
            && target.RunsLocally
            && !AllowsLocalSelfShutdown())
        {
            throw new InvalidOperationException(
                "Power Management: " + action.ToLowerInvariant() + " against the local NodePilot host is blocked by default. " +
                "Set PowerManagement:AllowLocalSelfShutdown=true to permit, or target a remote machine.");
        }
    }

    protected override string BuildScript(JsonElement config, StepExecutionContext context)
    {
        // Do not default `action` server-side. PowerManagement only has destructive
        // actions (shutdown/restart/logoff/hibernate); a silent default would turn a
        // malformed import or AI-generated workflow into a real shutdown of a remote
        // host (the self-shutdown guard below only catches localhost without creds).
        // The UI persists the visual default on first render so user-authored configs
        // are never empty; this throw is the defence-in-depth boundary for everything else.
        var action = config.GetStringOrNull("action")?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(action))
            throw new InvalidOperationException(
                "Power Management: 'action' is required (shutdown/restart/logoff/abort/hibernate)");

        var delay = config.TryGetProperty("delaySeconds", out var d) && d.TryGetInt32(out var ds)
            ? Math.Max(0, ds)
            : 0;
        // Default force=true: an interactive "An app is preventing shutdown" dialog would hang
        // a remote step. Deployments with a preserve-unsaved-work policy can set force=false.
        var force = config.GetBool("force", true);
        var message = config.GetStringOrNull("message");

        return action switch
        {
            "shutdown"  => GuardNativeExit(BuildShutdownInvocation("/s", delay, force, message)),
            "restart"   => GuardNativeExit(BuildShutdownInvocation("/r", delay, force, message)),
            "logoff"    => GuardNativeExit("& shutdown.exe /l"),
            // ERROR 1116 = "system is not currently being shut down" — abort against a
            // machine with no pending shutdown is a benign no-op, not a workflow failure.
            // Run via cmd /c to keep stderr off PowerShell's error stream, then translate
            // exit codes: 0 = aborted real shutdown, 1116 = nothing to abort (still ok),
            // everything else = surface as PS error.
            "abort"     => "$__out = & cmd.exe /c \"shutdown.exe /a 2>&1\"; if ($LASTEXITCODE -eq 0 -or $LASTEXITCODE -eq 1116) { Write-Output ($__out -join [Environment]::NewLine) } else { Write-Error ($__out -join [Environment]::NewLine) }",
            "hibernate" => GuardNativeExit("& shutdown.exe /h"),
            _ => throw new InvalidOperationException(
                $"Power Management: unknown action '{action}'. " +
                "Expected shutdown / restart / logoff / abort / hibernate.")
        };
    }

    private bool AllowsLocalSelfShutdown()
    {
        var raw = _configuration["PowerManagement:AllowLocalSelfShutdown"];
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string GuardNativeExit(string invocation) =>
        "$__npShutdown = " + invocation + "; " +
        "if ($LASTEXITCODE -ne 0) { throw \"shutdown.exe failed with exit code $($LASTEXITCODE): $($__npShutdown -join ' ')\" }; " +
        "$__npShutdown";

    private static string BuildShutdownInvocation(string opFlag, int delay, bool force, string? message)
    {
        // Windows implicitly enables /f whenever /t is positive.
        if (delay > 0 && !force)
            throw new InvalidOperationException(
                "Power Management: delaySeconds must be 0 when force is false. " +
                "Windows forces applications closed with a positive shutdown delay. " +
                "Use a separate Delay step before this activity to wait without forcing applications closed.");

        // shutdown.exe flags are bare tokens in PowerShell — the PS parser does not try to
        // resolve them as cmdlets because of the `&` call operator. Only the user-supplied
        // message needs PowerShellQuoter because it can contain apostrophes.
        var parts = new List<string> { "& shutdown.exe", opFlag };
        if (force) parts.Add("/f");
        parts.Add("/t");
        parts.Add(delay.ToString());
        if (!string.IsNullOrWhiteSpace(message))
        {
            parts.Add("/c");
            parts.Add(PowerShellQuoter.Literal(message));
        }
        return string.Join(" ", parts);
    }
}
