using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NodePilot.Scheduler.Sources;

/// <summary>
/// Polls a database query on a fixed interval. Fires when the first column of the first row
/// changes compared to the previous poll (so the query should return a sentinel like
/// MAX(Id) or a row-count). Config keys:
///   connectionString (optional) — inline connection string; blocked unless
///     <c>Trigger:Database:RequireConnectionRef</c>=false.
///   connectionRef (optional) — name of a pre-registered connection string stored under
///     <c>Trigger:Database:Connections:{name}</c> in appsettings. Preferred over inline.
///   provider (optional) — "sqlserver" | "sqlite", default "sqlserver"
///   query (required)
///   intervalSeconds (optional, default 30, min 5)
///
/// H-13: When <c>Trigger:Database:RequireConnectionRef</c> is missing or true, inline connectionStrings
/// are rejected so a workflow author cannot embed plaintext DB credentials in a trigger
/// config. Admins whitelist connection strings in appsettings via
/// <c>Trigger:Database:Connections:Prod="Server=..."</c>, and workflows reference them
/// by name: <c>{ "connectionRef": "Prod" }</c>. Mirrors the SqlActivity model.
/// </summary>
public class DatabaseTriggerSource : ITriggerSource
{
    public string ActivityType => "databaseTrigger";

    private readonly ILogger<DatabaseTriggerSource> _logger;
    private readonly IConfiguration _config;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private TriggerContext? _ctx;

    // Test hook: invoked after every poll completes (success or error). Lets the
    // unit-tests deterministically wait for the first poll to seed `lastSentinel`
    // before mutating the underlying table, instead of sleeping for the 5-second
    // poll interval. Production code never assigns this — only tests do via
    // InternalsVisibleTo.
    internal Action? OnPollCompletedForTest;

    public DatabaseTriggerSource(ILogger<DatabaseTriggerSource> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public Task StartAsync(TriggerContext context, CancellationToken ct)
    {
        _ctx = context;
        var cfg = context.Config;
        var connInline = cfg.TryGetProperty("connectionString", out var cs) ? cs.GetString() : null;
        var connRef = cfg.TryGetProperty("connectionRef", out var cr) ? cr.GetString() : null;
        var query = cfg.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("DatabaseTrigger: 'query' is required");

        // H-1 (security audit 2026-05-15): {{var}} templates in the trigger query are
        // always a workflow-author mistake — the trigger fires *outside* a workflow run,
        // so there is no upstream step or manual-trigger parameter to substitute. A residue
        // would either land literally in CommandText (and break the DB syntax) or, if the
        // engine ever grows pre-fire resolution, become an injection vector. Reject the
        // template at registration time so the operator sees the misuse immediately.
        if (query.Contains("{{", StringComparison.Ordinal) && query.Contains("}}", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "DatabaseTrigger: 'query' must not contain {{...}} templates. Trigger queries run before any "
                + "workflow step exists and have no variable context. Embed literal SQL only — pass dynamic "
                + "values through the workflow definition instead.");

        var requireRef = RequireConnectionRef();
        string connStr = ResolveConnectionString(connRef, connInline, requireRef);

        var provider = (cfg.TryGetProperty("provider", out var p) ? p.GetString() : null)?.ToLowerInvariant() ?? "sqlserver";
        var interval = Math.Max(5, cfg.TryGetProperty("intervalSeconds", out var i) && i.TryGetInt32(out var iv) ? iv : 30);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Own the poll loop as a tracked task on the source itself instead of spawning an
        // unmanaged Task.Run wrapper. DisposeAsync awaits this task for shutdown.
        _loopTask = PollLoopAsync(connStr, provider, query!, TimeSpan.FromSeconds(interval), _cts.Token);
        _logger.LogInformation("DatabaseTrigger: poll {Interval}s provider={Provider} source={Source}",
            interval, provider, !string.IsNullOrWhiteSpace(connRef) ? $"ref:{connRef}" : "inline");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves the DB connection string from either a config-whitelisted <c>connectionRef</c>
    /// or (when allowed) an inline <c>connectionString</c>. When <c>requireRef</c> is true,
    /// inline values are rejected outright so workflow JSON can't smuggle plaintext
    /// credentials into the running process.
    /// </summary>
    private string ResolveConnectionString(string? connRef, string? connInline, bool requireRef)
    {
        if (!string.IsNullOrWhiteSpace(connRef))
        {
            var fromConfig = _config[$"Trigger:Database:Connections:{connRef}"];
            if (string.IsNullOrWhiteSpace(fromConfig))
                throw new InvalidOperationException(
                    $"DatabaseTrigger: connectionRef '{connRef}' is not defined in " +
                    "Trigger:Database:Connections.");
            return fromConfig;
        }

        if (!string.IsNullOrWhiteSpace(connInline))
        {
            if (requireRef)
                throw new InvalidOperationException(
                    "DatabaseTrigger: inline connectionString is disabled " +
                    "(Trigger:Database:RequireConnectionRef=true). Use connectionRef with a " +
                    "name registered under Trigger:Database:Connections.");
            return connInline;
        }

        throw new InvalidOperationException(
            "DatabaseTrigger: either 'connectionString' or 'connectionRef' is required.");
    }

    private bool RequireConnectionRef()
    {
        var configured = _config["Trigger:Database:RequireConnectionRef"];
        return string.IsNullOrWhiteSpace(configured)
            || !string.Equals(configured, "false", StringComparison.OrdinalIgnoreCase);
    }

    private async Task PollLoopAsync(string connStr, string provider, string query, TimeSpan interval, CancellationToken ct)
    {
        string? lastSentinel = null;
        var typeTag = new KeyValuePair<string, object?>("trigger_type", "databaseTrigger");
        while (!ct.IsCancellationRequested)
        {
            var pollSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await using var conn = CreateConnection(provider, connStr);
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = query;
                var result = await cmd.ExecuteScalarAsync(ct);
                var sentinel = result?.ToString() ?? "";
                if (lastSentinel is not null && sentinel != lastSentinel)
                {
                    _ = _ctx!.OnFire(new Dictionary<string, string>
                    {
                        ["dbSentinel"] = sentinel,
                        ["dbPrevious"] = lastSentinel,
                    });
                }
                lastSentinel = sentinel;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SchedulerMetrics.TriggerPollErrors.Add(1, typeTag,
                    new KeyValuePair<string, object?>("error_class", ex.GetType().Name));
                // M8: scrub Password=/Pwd= segments from exception messages before logging.
                // SqlException and SqliteException routinely echo the failing connection
                // string, which for workflow-authored triggers can include plaintext DPAPI
                // creds. Use the same regex the OutputRedactor uses elsewhere so the scrub
                // is consistent. Log as a plain Information/Warning message — we keep the
                // exception's type + scrubbed message, but don't pass `ex` itself (which would
                // let the Serilog/OTel sink re-serialize the original message).
                var scrubbed = ScrubConnectionString(ex.Message);
                _logger.LogWarning("DatabaseTrigger poll failed ({Type}): {Message}", ex.GetType().Name, scrubbed);
            }
            finally
            {
                SchedulerMetrics.TriggerPollDuration.Record(pollSw.Elapsed.TotalMilliseconds, typeTag);
                OnPollCompletedForTest?.Invoke();
            }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static DbConnection CreateConnection(string provider, string connStr) => provider switch
    {
        "sqlite" => new SqliteConnection(connStr),
        _ => new SqlConnection(connStr),
    };

    private static readonly System.Text.RegularExpressions.Regex _connStrSecretRegex = new(
        @"(?i)\b(password|pwd)\s*=\s*[^;]+",
        System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static string ScrubConnectionString(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        try { return _connStrSecretRegex.Replace(message, "$1=***"); }
        catch { return "(error message suppressed: could not scrub potential secrets)"; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) { await _cts.CancelAsync(); try { if (_loopTask is not null) await _loopTask; } catch { /* ignore */ } _cts.Dispose(); }
    }
}
