using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Polls a condition until it is met or a timeout elapses — the SCOrch "Monitor" pattern
/// equivalent. **Hybrid** (like <see cref="RunScriptActivity"/>): without a target machine, or
/// with a loopback host and no credential, polling runs locally in the API process; with a real
/// target machine a WinRM session is opened for each poll. We inherit from
/// <see cref="BaseRemoteActivity"/> for machine/credential resolution and the localhost-bypass
/// behavior, but override <see cref="ExecuteAsync"/> directly because BuildScript only models a
/// single one-shot run.
///
/// <para>
/// <b>conditionType</b> chooses between free-form script mode and four typed sub-modes
/// (added 2026-05-17 to cover cases where a dynamic value couldn't be plugged into the
/// <c>script</c> field):
/// <list type="bullet">
///   <item><c>script</c> (default, backward-compatible) — any PowerShell expression in the
///     <c>script</c> field, boolean-cast as before. <c>{{...}}</c> templates are still forbidden
///     in this field (injection protection).</item>
///   <item><c>pathExists</c> — <c>path</c> (string). Resolves to <c>Test-Path</c>.</item>
///   <item><c>serviceRunning</c> — <c>serviceName</c> (string). Checks <c>(Get-Service).Status -eq 'Running'</c>.</item>
///   <item><c>portOpen</c> — <c>host</c> (string) + <c>port</c> (int). Checks a bounded <c>TcpClient</c> connect.</item>
///   <item><c>httpOk</c> — <c>url</c> (string). Checks for an HTTP 2xx via <c>Invoke-WebRequest</c>.</item>
/// </list>
/// Sub-mode fields may contain <c>{{step.param.x}}</c> templates — the engine's resolver
/// substitutes those <i>before</i> we assemble the PowerShell expression. Values are passed
/// through <see cref="PowerShellQuoter.Literal"/> verbatim as a single-quoted PowerShell
/// literal in the test call, so they are injection-safe.
/// </para>
///
/// Config:
///   conditionType     string, optional — see above (default "script")
///   script            string, required iff conditionType=script — PowerShell boolean expression
///   path              string, required iff conditionType=pathExists
///   serviceName       string, required iff conditionType=serviceRunning
///   host              string, required iff conditionType=portOpen
///   port              int,    required iff conditionType=portOpen — 1..65535
///   url               string, required iff conditionType=httpOk
///   intervalSeconds   int, default 5    — gap between two polls (minimum 1)
///   timeoutSeconds    int, default 300  — overall time budget; Success=false once exceeded
/// </summary>
public class WaitForConditionActivity : BaseRemoteActivity
{
    public override string ActivityType => "waitForCondition";

    public WaitForConditionActivity(
        IRemoteSessionFactory sessionFactory,
        ICredentialStore credentialStore,
        NodePilot.Data.NodePilotDbContext db,
        PowerShellEngineFactory engineFactory,
        IConfiguration configuration)
        : base(sessionFactory, credentialStore, db, engineFactory, configuration) { }

    public override async Task<ActivityResult> ExecuteAsync(
        StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        string conditionExpression;
        try
        {
            conditionExpression = BuildConditionExpression(config);
            ValidateNetworkTarget(config);
        }
        catch (InvalidOperationException ex)
        {
            // Validation errors come through as a clean Failed result instead of an
            // exception so the step shows up as Failed (not Error) in the timeline —
            // matches how the legacy "script must not contain {{...}}" branch behaved.
            return new ActivityResult { Success = false, ErrorOutput = ex.Message };
        }

        var interval = Math.Max(1, config.TryGetProperty("intervalSeconds", out var iv) && iv.TryGetInt32(out var ivi) ? ivi : 5);
        var timeout = Math.Max(1, config.TryGetProperty("timeoutSeconds", out var to) && to.TryGetInt32(out var toi) ? toi : 300);

        // Wrapper that casts the expression to a boolean and writes it out behind a marker.
        // The user's script may also produce diagnostic output on the side — we carry that
        // along as a `lastResult` hint in the timeout error message.
        var wrapped = $@"
$__npResult = $null
try {{
    $__npResult = [bool]({conditionExpression})
}} catch {{
    Write-Error $_.Exception.Message
    $__npResult = $false
}}
Write-Output ('###NODEPILOT_COND:' + $__npResult + '###')";

        // Hybrid resolution (matches RunScript): no target machine → local; a machine with a
        // loopback host and no credential → local; otherwise remote via WinRM.
        var machine = context.ResolvedMachine;
        Credential? credential = null;
        if (machine is not null)
        {
            var credentialId = context.CredentialId ?? machine.DefaultCredentialId;
            if (credentialId is not null)
                credential = await _credentialStore.GetAsync(credentialId.Value, ct);
        }
        var isLocalhost = machine is null
            || (credential is null && IsLoopbackHostname(machine.Hostname));
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        int attempts = 0;
        string? lastOutput = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // A fresh session per poll instead of one held for the whole wait: WinRM servers cap
        // shells per user (default MaxShellsPerUser = 30, often lowered to 10-15 via GPO). A
        // session held for the entire wait duration (up to 300s) would occupy a shell slot the
        // whole time — several parallel waitForCondition steps against the same host would blow
        // through that quota immediately and block every other step targeting that host. The
        // connection pool reuses sessions between polls (idle TTL 120s, poll interval typically
        // 1-5s → almost always a pool hit), so the authentication cost is only paid on the first poll.
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            attempts++;
            RemoteExecutionResult result;
            if (isLocalhost)
            {
                var localEngine = _engineFactory.GetEngine("auto");
                var psRes = await localEngine.ExecuteAsync(new PowerShellExecutionRequest
                {
                    ScriptText = wrapped,
                    Timeout = TimeSpan.FromSeconds(Math.Max(5, interval * 2)),
                }, ct);
                result = new RemoteExecutionResult
                {
                    Success = psRes.Success,
                    Output = psRes.Output,
                    ErrorOutput = psRes.Error,
                    Duration = psRes.Duration,
                };
            }
            else
            {
                // machine is guaranteed non-null here — otherwise isLocalhost would be true.
                await using var session = await _sessionFactory.CreateSessionAsync(machine!, credential, ct);
                result = await session.ExecuteScriptAsync(wrapped, Math.Max(5, interval * 2), ct);
            }
            lastOutput = result.Output;

            if (ExtractBoolean(result.Output))
            {
                sw.Stop();
                return new ActivityResult
                {
                    Success = true,
                    Output = $"Condition met after {attempts} attempt(s) in {sw.Elapsed.TotalSeconds:F1}s",
                    Duration = sw.Elapsed,
                    OutputParameters = new Dictionary<string, string>
                    {
                        ["attempts"] = attempts.ToString(),
                        ["elapsedSeconds"] = sw.Elapsed.TotalSeconds.ToString("F1"),
                        ["lastResult"] = "true",
                    },
                };
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            var sleep = TimeSpan.FromSeconds(interval);
            if (sleep > remaining) sleep = remaining;
            try { await Task.Delay(sleep, ct); }
            catch (OperationCanceledException) { break; }
        }

        sw.Stop();
        // D2: cap the tail-of-last-output to 2 KB so a chatty polling script does not
        // pump hundreds of KB into ErrorOutput, the SignalR stream, and the support log.
        var lastTrimmed = lastOutput?.Trim();
        if (lastTrimmed is { Length: > MaxLastOutputChars })
            lastTrimmed = lastTrimmed[..MaxLastOutputChars] + "…(truncated)";
        return new ActivityResult
        {
            Success = false,
            ErrorOutput = $"Timeout after {timeout}s ({attempts} attempts). Last script output: {lastTrimmed ?? "(none)"}",
            Duration = sw.Elapsed,
            OutputParameters = new Dictionary<string, string>
            {
                ["attempts"] = attempts.ToString(),
                ["elapsedSeconds"] = sw.Elapsed.TotalSeconds.ToString("F1"),
                ["lastResult"] = "false",
            },
        };
    }

    private const int MaxLastOutputChars = 2 * 1024;

    private void ValidateNetworkTarget(JsonElement config)
    {
        var conditionType = (config.GetStringOrNull("conditionType") ?? "script").Trim().ToLowerInvariant();
        if (conditionType == "portopen")
        {
            var host = config.GetStringOrNull("host")!; // presence is validated by the builder first
            NetworkGuard.RequireExplicitlyAllowlistedHost(
                _configuration ?? throw new InvalidOperationException("WaitForCondition: network policy configuration is unavailable."),
                host,
                "WaitForCondition portOpen");
            return;
        }

        if (conditionType == "httpok")
        {
            var url = config.GetStringOrNull("url")!; // presence is validated by the builder first
            var configuration = _configuration
                ?? throw new InvalidOperationException("WaitForCondition: network policy configuration is unavailable.");
            NetworkGuard.ValidateUrl(configuration, url);
            var uri = new Uri(url, UriKind.Absolute);
            NetworkGuard.RequireExplicitlyAllowlistedHost(configuration, uri.Host, "WaitForCondition httpOk");
        }
    }

    // BuildScript is never called by our overridden ExecuteAsync; we still have to implement
    // the abstract member. Nothing should ever reach this code path.
    protected override string BuildScript(JsonElement config, StepExecutionContext context)
        => throw new NotSupportedException("WaitForConditionActivity overrides ExecuteAsync and does not use BuildScript.");

    private static bool ExtractBoolean(string? output)
    {
        if (string.IsNullOrEmpty(output)) return false;
        var idx = output.LastIndexOf("###NODEPILOT_COND:", StringComparison.Ordinal);
        if (idx < 0) return false;
        var end = output.IndexOf("###", idx + 18, StringComparison.Ordinal);
        if (end < 0) return false;
        var raw = output.Substring(idx + 18, end - (idx + 18)).Trim();
        return string.Equals(raw, "True", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds the PowerShell boolean expression that gets <c>[bool]</c>-cast inside the
    /// polling wrapper. Routes by <c>conditionType</c>:
    /// <list type="bullet">
    ///   <item><c>script</c> — raw expression from <c>script</c> field. <c>{{...}}</c>
    ///     residue rejected (would land unquoted in the cast, see legacy guard).</item>
    ///   <item><c>pathExists</c>, <c>serviceRunning</c>, <c>portOpen</c>, <c>httpOk</c> —
    ///     dedicated typed builders. Each accepts dynamic values (already resolved by the
    ///     engine before this point) and runs them through <see cref="PowerShellQuoter.Literal"/>
    ///     so the value lands as a single-quoted PS literal, immune to injection even when
    ///     the upstream output contained apostrophes or shell metacharacters.</item>
    /// </list>
    /// </summary>
    internal static string BuildConditionExpression(JsonElement config)
    {
        var rawType = config.GetStringOrNull("conditionType");
        var conditionType = string.IsNullOrWhiteSpace(rawType) ? "script" : rawType.Trim().ToLowerInvariant();

        return conditionType switch
        {
            "script" => BuildFreeFormScript(config),
            "pathexists" => BuildPathExistsScript(config),
            "servicerunning" => BuildServiceRunningScript(config),
            "portopen" => BuildPortOpenScript(config),
            "httpok" => BuildHttpOkScript(config),
            _ => throw new InvalidOperationException(
                $"WaitForCondition: unknown conditionType '{rawType}' " +
                "(expected 'script', 'pathExists', 'serviceRunning', 'portOpen', or 'httpOk')."),
        };
    }

    private static string BuildFreeFormScript(JsonElement config)
    {
        var script = config.GetStringOrNull("script");
        if (string.IsNullOrWhiteSpace(script))
            throw new InvalidOperationException(
                "WaitForCondition: 'script' is required when conditionType='script' (or unset).");

        // The engine deliberately keeps `script` out of the template-resolution pass (see
        // StepRunner.FieldsNotToResolve). Residual {{...}} placeholders mean a workflow
        // author tried to template a value into the PS expression — which would land
        // unquoted inside a `[bool](...)` cast and let an upstream output close the cast
        // and inject arbitrary PS. Fail closed and point the author at the typed sub-modes
        // (pathExists / serviceRunning / portOpen / httpOk) for dynamic values — those
        // accept {{...}} safely because we build the script with PS-quoted literals.
        if (script.Contains("{{", StringComparison.Ordinal) && script.Contains("}}", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "WaitForCondition: 'script' must not contain {{...}} templates. "
                + "The script body is embedded raw into a PowerShell [bool] cast — template values "
                + "would break out of the cast and become an injection vector. "
                + "Use conditionType='pathExists'/'serviceRunning'/'portOpen'/'httpOk' for dynamic "
                + "values, or assemble the full condition in an upstream runScript step.");

        return script;
    }

    private static string BuildPathExistsScript(JsonElement config)
    {
        var path = config.GetStringOrNull("path");
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                "WaitForCondition: 'path' is required when conditionType='pathExists'.");
        // Test-Path returns $true for both files and directories. -LiteralPath so wildcards
        // in upstream-supplied paths don't get glob-expanded behind the author's back.
        return $"Test-Path -LiteralPath {PowerShellQuoter.Literal(path)}";
    }

    private static string BuildServiceRunningScript(JsonElement config)
    {
        var name = config.GetStringOrNull("serviceName");
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException(
                "WaitForCondition: 'serviceName' is required when conditionType='serviceRunning'.");
        // Wrapped in try/catch via the outer wrapper, so a missing service raises an error
        // → caught → $__npResult=$false → poll continues.
        return $"((Get-Service -Name {PowerShellQuoter.Literal(name)} -ErrorAction Stop).Status -eq 'Running')";
    }

    private static string BuildPortOpenScript(JsonElement config)
    {
        var host = config.GetStringOrNull("host");
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException(
                "WaitForCondition: 'host' is required when conditionType='portOpen'.");
        if (!config.TryGetProperty("port", out var portEl) || !portEl.TryGetInt32(out var port))
            throw new InvalidOperationException(
                "WaitForCondition: 'port' (integer) is required when conditionType='portOpen'.");
        if (port < 1 || port > 65535)
            throw new InvalidOperationException(
                $"WaitForCondition: 'port' must be 1..65535, got {port}.");
        // Direct TcpClient connect with a 1.5s timeout — Test-NetConnection's internal
        // probes can exceed intervalSeconds=1, causing polls to never complete a single
        // attempt before the outer timeout. The script returns $true if the TCP handshake
        // succeeded within 1.5s, $false otherwise (no exceptions leak out).
        return $"(& {{ $__c = New-Object System.Net.Sockets.TcpClient; try {{ $__t = $__c.ConnectAsync({PowerShellQuoter.Literal(host)}, {port}); $__ok = $__t.Wait(1500); if ($__ok -and $__c.Connected) {{ $true }} else {{ $false }} }} catch {{ $false }} finally {{ $__c.Close() }} }})";
    }

    private static string BuildHttpOkScript(JsonElement config)
    {
        var url = config.GetStringOrNull("url");
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                "WaitForCondition: 'url' is required when conditionType='httpOk'.");
        // PowerShell `try { ... } catch { ... }` is a STATEMENT — it cannot live inside an
        // expression context like `[bool](...)`. The fix is to wrap in `& { ... }`: the
        // call operator invoking a script block IS an expression, and the script block's
        // last-evaluated value (the bool from try OR the $false from catch) becomes the
        // call's output. Status-code lower bound 200 + upper bound 300 covers 2xx OK.
        return $"(& {{ try {{ $__r = Invoke-WebRequest -Uri {PowerShellQuoter.Literal(url)} -UseBasicParsing -MaximumRedirection 0 -TimeoutSec 5 -ErrorAction Stop; ($__r.StatusCode -ge 200 -and $__r.StatusCode -lt 300) }} catch {{ $false }} }})";
    }
}
