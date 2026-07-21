using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Activities;

public abstract class BaseRemoteActivity : IActivityExecutor
{
    // Protected rather than private, so subclasses with special flows (e.g. the poll loop in
    // WaitForConditionActivity) can reuse the shared session/credential infrastructure without
    // going through the base class's standard ExecuteAsync path.
    protected readonly IRemoteSessionFactory _sessionFactory;
    protected readonly ICredentialStore _credentialStore;
    protected readonly NodePilot.Data.NodePilotDbContext _db;
    protected readonly PowerShellEngineFactory _engineFactory;
    protected readonly IConfiguration? _configuration;

    public abstract string ActivityType { get; }

    protected BaseRemoteActivity(
        IRemoteSessionFactory sessionFactory,
        ICredentialStore credentialStore,
        NodePilot.Data.NodePilotDbContext db,
        PowerShellEngineFactory engineFactory)
        : this(sessionFactory, credentialStore, db, engineFactory, null) { }

    protected BaseRemoteActivity(
        IRemoteSessionFactory sessionFactory,
        ICredentialStore credentialStore,
        NodePilot.Data.NodePilotDbContext db,
        PowerShellEngineFactory engineFactory,
        IConfiguration? configuration)
    {
        _sessionFactory = sessionFactory;
        _credentialStore = credentialStore;
        _db = db;
        _engineFactory = engineFactory;
        _configuration = configuration;
    }

    public virtual async Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        // Prefer pre-resolved machine from engine (handles both registered and ad-hoc hostnames)
        var machine = context.ResolvedMachine;

        // Fall back to DB lookup by GUID if engine didn't pre-resolve
        if (machine is null && context.TargetMachineId is not null)
        {
            machine = await _db.ManagedMachines.FindAsync([context.TargetMachineId.Value], ct);
        }

        if (machine is null)
            return new ActivityResult { Success = false, ErrorOutput = "No target machine specified" };

        // Credential is optional. If none set, WinRM uses NodePilot process identity.
        var credentialId = context.CredentialId ?? machine.DefaultCredentialId;
        Credential? credential = null;
        if (credentialId is not null)
            credential = await _credentialStore.GetAsync(credentialId.Value, ct);

        var script = BuildScript(config, context);
        var timeoutSeconds = PowerShellOperation.TimeoutSecondsFromConfig(config);

        // Localhost bypass: WinRM with implicit credentials fails on localhost (error 0x8009030e).
        // When targeting localhost without explicit credentials, run the script directly via the
        // local PowerShell engine — same path that RunScriptActivity uses for local scripts.
        //
        // This is a *product feature*, not a bug: NodePilot doubles as a self-service
        // orchestrator for the host it runs on, so localhost targets must always work. The
        // security review flagged the blast radius (in-process PowerShell as the service
        // account can read jwt-secret.key and decrypt DPAPI credentials) — that is an
        // acknowledged, intentional trade-off. Do not re-introduce a guard here: the product
        // owner has explicitly decided localhost execution stays on in every environment.
        //
        // The DETECTION is hardened though (H14): the old string compare missed "127.0.0.01",
        // "127.1", "2130706433", trailing whitespace, and IPv6 zone-ids. Those all resolve to
        // loopback at the OS layer, so a machine registered with a creative hostname was
        // shipping scripts over WinRM with the service-account Kerberos ticket instead of
        // in-process — defeating the exact guarantee the bypass was there to provide.
        var isLocalhost = credential is null && IsLoopbackHostname(machine.Hostname);

        if (isLocalhost)
        {
            // Cluster-mode warning: a localhost step runs in-process on whichever node
            // happens to be the leader at fire time. After failover the step lands on a
            // different physical host with a potentially different filesystem, env vars,
            // installed software, etc. Operators who rely on cluster-equivalence learn
            // about this here rather than from a confusing post-failover bug report.
            // Emitted via the engine's ActivitySource so it surfaces in OpenTelemetry
            // tracing without requiring an ILogger reference inside NodePilot.Core
            // (which is zero-deps by convention, see CLAUDE.md).
            if (_configuration?.GetValue<bool>("Cluster:Enabled") ?? false)
            {
                var nodeId = _configuration["Cluster:NodeId"] ?? Environment.MachineName;
                Activity.Current?.AddEvent(new ActivityEvent("cluster.localhost_step",
                    tags: new ActivityTagsCollection
                    {
                        ["nodepilot.cluster.node_id"] = nodeId,
                        ["nodepilot.warning"] = "Localhost step output may differ after failover."
                    }));
            }

            var localEngine = _engineFactory.GetEngine("auto");
            var psRequest = new PowerShellExecutionRequest
            {
                ScriptText = script,
                Timeout = PowerShellOperation.ToTimeSpan(timeoutSeconds),
            };
            var psResult = await localEngine.ExecuteAsync(psRequest, ct);
            // Both local engines wrap the script via PowerShellScriptWrapper, so stdout ends with
            // the ###NODEPILOT_*### marker block (exit code, params, error). The WinRM path has no
            // markers — strip them so PostProcess implementations that parse Output (e.g.
            // serviceManagement status → JSON) see identical input on both paths. The wrapper's
            // captured params are dropped for the same parity reason.
            var (cleanOutput, _, _) = PowerShellActivitySupport.ExtractMarkers(
                psResult.Output, context.StepId, NullLogger.Instance);
            return PostProcess(new ActivityResult
            {
                Success = psResult.Success,
                Output = cleanOutput,
                ErrorOutput = psResult.Error,
                Duration = psResult.Duration,
            }, config);
        }

        // Remote path via WinRM
        var sw = Stopwatch.StartNew();
        await using var session = await _sessionFactory.CreateSessionAsync(machine, credential, ct);
        var result = await session.ExecuteScriptAsync(script, timeoutSeconds, ct);
        sw.Stop();

        return PostProcess(new ActivityResult
        {
            Success = result.Success,
            Output = result.Output,
            ErrorOutput = result.ErrorOutput,
            Duration = sw.Elapsed
        }, config);
    }

    protected abstract string BuildScript(JsonElement config, StepExecutionContext context);

    /// <summary>
    /// Returns true when the given hostname points at the local machine — covers all the
    /// IPv4/IPv6 spellings (127.0.0.1, 127.0.0.01, 127.1, 2130706433, ::1, [::1], …), DNS
    /// names whose A/AAAA records resolve to a loopback address (e.g. "localhost.corp.lan"
    /// mapped to 127.0.0.1 in hosts-file) and punctuation-trimmed variants with trailing
    /// dots or whitespace. The previous string-compare check missed most of these and
    /// silently routed "loopback" traffic through the real WinRM stack.
    ///
    /// Failed DNS lookups return false — we don't want to hit the network for every step,
    /// and if resolution fails here the WinRM path will emit a cleaner error anyway.
    /// </summary>
    internal static bool IsLoopbackHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return false;
        var trimmed = hostname.Trim().Trim('.');
        // Normalize the bracketed + zone-id IPv6 literal forms — "[::1]", "::1%lo0".
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) trimmed = trimmed[1..^1];
        var percent = trimmed.IndexOf('%');
        if (percent >= 0) trimmed = trimmed[..percent];
        if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(trimmed, out var direct))
            return IPAddress.IsLoopback(direct);

        // DNS fallback: if the name resolves to an all-loopback answer, treat as localhost.
        // Wrapped in try/catch because DNS can fail for many reasons (offline host, no such
        // name) and a failure here must fall back to the normal remote path — NOT crash the
        // step.
        try
        {
            var addresses = Dns.GetHostAddresses(trimmed);
            return addresses.Length > 0 && addresses.All(IPAddress.IsLoopback);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Optional hook for subclasses to transform the raw remote/local PowerShell result —
    /// e.g. parse structured markers from Output into <see cref="ActivityResult.OutputParameters"/>
    /// or adjust Success based on exit codes. Default is pass-through.
    /// </summary>
    protected virtual ActivityResult PostProcess(ActivityResult raw, JsonElement config) => raw;

    /// <summary>
    /// Helper for PostProcess overrides: copies a string field from the JSON object into the
    /// target dictionary. A <c>ValueKind.String</c> is copied via <c>GetString()</c>, Null/Undefined
    /// is stored as an empty string, and every other kind is serialized via <c>GetRawText()</c>.
    /// </summary>
    protected static void CopyStringField(JsonElement obj, string sourceKey, IDictionary<string, string> dest, string destKey)
        => PowerShellOperation.CopyStringField(obj, sourceKey, dest, destKey);
}
