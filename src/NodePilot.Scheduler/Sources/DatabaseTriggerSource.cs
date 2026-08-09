using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Triggers;

namespace NodePilot.Scheduler.Sources;

/// <summary>
/// Polls a database query on a fixed interval and fires when the first column of the first row
/// changes compared to the previous poll — so the query should return a sentinel such as MAX(Id)
/// or a row count.
///
/// <para>Config parsing, defaults, validation and connection resolution live in
/// <see cref="DatabaseTriggerSettings"/>, shared with the node executor
/// (<c>NodePilot.Engine.Triggers.DatabaseTrigger</c>) so a documented key cannot be honoured by one
/// runtime and silently dropped by the other. This source used to read its interval from
/// <c>intervalSeconds</c> while the designer, the docs and the node executor all wrote
/// <c>pollingIntervalSeconds</c> — the configured interval never reached the poll loop.</para>
/// </summary>
public class DatabaseTriggerSource : ITriggerSource
{
    public string ActivityType => "databaseTrigger";

    /// <summary>
    /// The poll loop IS the subscription: if that task ended while the orchestrator still holds
    /// this source, nothing will ever poll again and the trigger is silently dead. The loop
    /// swallows per-iteration exceptions itself, so a completed task means it exited for a reason
    /// it could not handle. Pure field reads — no I/O, per the <see cref="ITriggerSource.Health"/>
    /// contract.
    /// </summary>
    public TriggerHealth Health =>
        _disposed || _loopTask is null or { IsCompleted: false }
            ? TriggerHealth.Healthy
            : TriggerHealth.Faulted($"poll loop ended ({_loopTask.Status})");

    private readonly ILogger<DatabaseTriggerSource> _logger;
    private readonly IConfiguration _config;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private TriggerContext? _ctx;

    // Set by DisposeAsync so a torn-down source never reports unhealthy — the orchestrator
    // disposes on its own teardown paths (leadership loss, shutdown, config change) and must
    // not see those as faults worth re-registering.
    private volatile bool _disposed;

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
        var settings = DatabaseTriggerSettings.Parse(context.Config);
        var connStr = settings.ResolveConnectionString(
            name => _config[$"Trigger:Database:Connections:{name}"],
            RequireConnectionRef());

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Own the poll loop as a tracked task on the source itself instead of spawning an
        // unmanaged Task.Run wrapper. DisposeAsync awaits this task for shutdown.
        _loopTask = PollLoopAsync(
            connStr, settings.Provider, settings.Query,
            TimeSpan.FromSeconds(settings.PollingIntervalSeconds), _cts.Token);
        _logger.LogInformation("DatabaseTrigger: poll {Interval}s provider={Provider} source={Source}",
            settings.PollingIntervalSeconds, settings.Provider,
            !string.IsNullOrWhiteSpace(settings.ConnectionRef) ? $"ref:{settings.ConnectionRef}" : "inline");
        return Task.CompletedTask;
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
                    TriggerFireObserver.Observe(
                        _ctx!.OnFire(new Dictionary<string, string>
                        {
                            ["dbSentinel"] = sentinel,
                            ["dbPrevious"] = lastSentinel,
                        }),
                        _logger, ActivityType, _ctx.WorkflowId, _ctx.NodeId);
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
        _disposed = true;
        if (_cts is not null) { await _cts.CancelAsync(); try { if (_loopTask is not null) await _loopTask; } catch { /* ignore */ } _cts.Dispose(); }
    }
}
