using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Engine.Security;

namespace NodePilot.Api.Hubs;

public class SignalRExecutionNotifier : BackgroundService, IExecutionNotifier
{
    private static readonly KeyValuePair<string, object?> StepStartedTag = new("event_type", "StepStarted");
    private static readonly KeyValuePair<string, object?> StepCompletedTag = new("event_type", "StepCompleted");
    private static readonly KeyValuePair<string, object?> ExecutionStatusTag = new("event_type", "ExecutionStatusChanged");
    private static readonly KeyValuePair<string, object?> StepPausedTag = new("event_type", "StepPaused");
    private static readonly KeyValuePair<string, object?> StepResumedTag = new("event_type", "StepResumed");

    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(50);
    private const int MaxBatchSize = 1024;

    private readonly IHubContext<ExecutionHub> _hub;
    private readonly ILogger<SignalRExecutionNotifier> _logger;
    private readonly Channel<QueuedLiveEvent> _events;
    private readonly Func<Guid, Guid, bool> _hasSubscribers;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly Func<Guid, CancellationToken, Task<Guid?>> _resolveFolder;
    private readonly OutputRedactor _redactor;

    // workflowId → folderId, so the live-ops feed's per-event RBAC filter doesn't hit the DB on
    // every status transition. Folder moves are rare; a stale entry can at worst send a
    // status-only event (no payload) to a connection whose scope no longer matches — acceptable
    // for the live-ops feed, same single-snapshot RBAC posture as JoinExecution/JoinWorkflow.
    private readonly ConcurrentDictionary<Guid, Guid> _workflowFolderCache = new();

    public SignalRExecutionNotifier(
        IHubContext<ExecutionHub> hub,
        ILogger<SignalRExecutionNotifier>? logger = null,
        Func<Guid, Guid, bool>? hasSubscribers = null,
        IServiceScopeFactory? scopeFactory = null,
        Func<Guid, CancellationToken, Task<Guid?>>? folderResolver = null,
        OutputRedactor? redactor = null)
    {
        _hub = hub;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SignalRExecutionNotifier>.Instance;
        _hasSubscribers = hasSubscribers ?? ExecutionHub.HasSubscribers;
        _scopeFactory = scopeFactory;
        _resolveFolder = folderResolver ?? DefaultResolveFolderAsync;
        _redactor = redactor ?? new OutputRedactor();
        // DropNewest instead of DropOldest: under burst load (large workflows with many
        // parallel steps) the oldest events include early StepStarted/StepCompleted pairs.
        // Dropping those leaves steps permanently "Running" in the Live View. DropNewest
        // preserves already-landed state and only loses trailing updates — the final
        // ExecutionStatusChanged event is far more likely to survive, which lets the
        // frontend finalize the spinner for any steps whose Completed event was dropped.
        _events = Channel.CreateBounded<QueuedLiveEvent>(
            new BoundedChannelOptions(32768)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropNewest,
                AllowSynchronousContinuations = false,
            });
    }

    /// <summary>
    /// Step-level events (StepStarted/StepCompleted/StepPaused/StepResumed) go only to the
    /// per-execution group. Clients watching the workflow firehose ("workflow-{id}") receive
    /// status-summary events only; they must call JoinExecution to subscribe to step detail.
    /// This limits the event flood when 50-100 executions run in parallel — without it every
    /// step from every concurrent run hits every open workflow-editor tab.
    /// </summary>
    private static string[] ExecutionOnlyGroups(Guid executionId)
        => [executionId.ToString()];

    /// <summary>
    /// Terminal status events go to both groups: the per-execution watcher and the
    /// per-workflow firehose. This lets the editor show status badges (Running/Succeeded/
    /// Failed) for all executions without requiring a JoinExecution call per run.
    /// </summary>
    private static string[] GroupsFor(Guid executionId, Guid workflowId)
        => [executionId.ToString(), $"workflow-{workflowId}"];

    public Task StepStartedAsync(Guid executionId, Guid workflowId, string stepId, string? stepName, string stepType, DateTime startedAt)
    {
        var (traceId, spanId) = CurrentContext();
        var evt = new StepStartedEvent(executionId, workflowId, stepId, stepName, stepType, startedAt, traceId, spanId);
        return EnqueueStepEventAsync(executionId, workflowId, new LiveEventBatchItem("StepStarted", evt), StepStartedTag).AsTask();
    }

    public Task StepCompletedAsync(Guid executionId, Guid workflowId, string stepId, string? stepName, ExecutionStatus status, string? output, string? errorOutput, DateTime completedAt, IReadOnlyDictionary<string, string>? outputParameters = null, string? traceOutput = null, string? stepType = null, DateTime? startedAt = null, string? outputVariable = null)
    {
        var (traceId, spanId) = CurrentContext();
        var pars = outputParameters?.Count > 0
            ? outputParameters.ToDictionary(
                pair => pair.Key,
                pair => _redactor.RedactNamedValue(pair.Key, pair.Value) ?? pair.Value)
            : null;
        var evt = new StepCompletedEvent(executionId, workflowId, stepId, stepName, status.ToString(), output, errorOutput, completedAt, traceId, spanId, pars, traceOutput, stepType, startedAt, outputVariable);
        return EnqueueStepEventAsync(executionId, workflowId, new LiveEventBatchItem("StepCompleted", evt), StepCompletedTag).AsTask();
    }

    public Task ExecutionStatusChangedAsync(Guid executionId, Guid workflowId, ExecutionStatus status, string? errorMessage, DateTime? completedAt)
    {
        // Status events feed both the per-execution/per-workflow watchers AND the live-ops feed.
        // Drop only when nobody at all is listening.
        if (!_hasSubscribers(executionId, workflowId) && !ExecutionHub.HasOpsFeedSubscribers())
            return Task.CompletedTask;

        var (traceId, _) = CurrentContext();
        var evt = new ExecutionStatusEvent(executionId, workflowId, status.ToString(), errorMessage, completedAt, traceId);

        if (_events.Writer.TryWrite(new QueuedLiveEvent(executionId, workflowId,
                new LiveEventBatchItem("ExecutionStatusChanged", evt), IsStatusEvent: true)))
            ApiMetrics.SignalRMessagesSent.Add(1, ExecutionStatusTag);

        return Task.CompletedTask;
    }

    public Task StepPausedAsync(Guid executionId, Guid workflowId, string stepId, string? stepName,
        IReadOnlyDictionary<string, string> variables, DateTime pausedAt, string reason)
    {
        var vars = variables.ToDictionary(
            pair => pair.Key,
            pair => _redactor.RedactNamedValue(pair.Key, pair.Value) ?? pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var evt = new StepPausedEvent(executionId, workflowId, stepId, stepName, vars, pausedAt, reason);
        return EnqueueStepEventAsync(executionId, workflowId, new LiveEventBatchItem("StepPaused", evt), StepPausedTag).AsTask();
    }

    public Task StepResumedAsync(Guid executionId, Guid workflowId, string stepId)
    {
        var evt = new StepResumedEvent(executionId, workflowId, stepId);
        return EnqueueStepEventAsync(executionId, workflowId, new LiveEventBatchItem("StepResumed", evt), StepResumedTag).AsTask();
    }

    /// <summary>
    /// Enqueues a step-level event (StepStarted/StepCompleted/StepPaused/StepResumed).
    /// Only checks the per-execution subscriber count — if no client has called
    /// JoinExecution for this run, the event is silently dropped rather than queued and
    /// sent to an empty group. This prevents 100-run bursts from filling the bounded
    /// channel with events that nobody will receive.
    /// </summary>
    private ValueTask EnqueueStepEventAsync(Guid executionId, Guid workflowId, LiveEventBatchItem item, KeyValuePair<string, object?> metricTag)
    {
        if (!ExecutionHub.HasGroupSubscribers(executionId.ToString()))
            return ValueTask.CompletedTask;

        if (_events.Writer.TryWrite(new QueuedLiveEvent(executionId, workflowId, item)))
            ApiMetrics.SignalRMessagesSent.Add(1, metricTag);

        return ValueTask.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _events.Reader.WaitToReadAsync(stoppingToken))
            {
                var batch = new List<QueuedLiveEvent>(MaxBatchSize);
                DrainAvailable(batch);
                if (batch.Count == 0) continue;

                try
                {
                    await Task.Delay(FlushInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                DrainAvailable(batch);
                await SendBatchAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    internal async Task FlushForTestAsync(CancellationToken ct = default)
    {
        var batch = new List<QueuedLiveEvent>(MaxBatchSize);
        DrainAvailable(batch);
        if (batch.Count > 0)
            await SendBatchAsync(batch, ct);
    }

    private void DrainAvailable(List<QueuedLiveEvent> batch)
    {
        while (batch.Count < MaxBatchSize && _events.Reader.TryRead(out var item))
            batch.Add(item);
    }

    private async Task SendBatchAsync(List<QueuedLiveEvent> batch, CancellationToken ct)
    {
        // Build all per-group sends first, then await them all concurrently via
        // Task.WhenAll. The old sequential foreach/await meant 200 parallel executions
        // produced 200 sequential SendAsync calls per flush — at ~1ms each that exceeds
        // the 50ms flush interval, causing event backlog and DropNewest losses.
        var sends = new List<Task>(batch.Count);
        foreach (var group in batch.GroupBy(e => (e.ExecutionId, e.WorkflowId, e.IsStatusEvent)))
        {
            var payload = new LiveEventsBatch(group.Select(e => e.Item).ToArray());

            if (group.Key.IsStatusEvent)
            {
                // Per-execution + per-workflow watchers (status badges in the editor).
                if (_hasSubscribers(group.Key.ExecutionId, group.Key.WorkflowId))
                    sends.Add(SendGroupSafeAsync(GroupsFor(group.Key.ExecutionId, group.Key.WorkflowId), payload, ct));

                // Live-ops "NOC" feed: resolve the workflow's folder, then send only to the
                // ops-feed connections whose RBAC scope covers it. Status events ONLY reach
                // this feed — step detail never does.
                if (ExecutionHub.HasOpsFeedSubscribers())
                {
                    var folderId = await _resolveFolder(group.Key.WorkflowId, ct);
                    if (folderId is not null)
                    {
                        var conns = ExecutionHub.GetOpsFeedConnections(folderId.Value);
                        if (conns.Count > 0)
                            sends.Add(SendConnectionsSafeAsync(conns, payload, ct));
                    }
                }
            }
            else
            {
                // Step-level events go only to the per-execution group.
                if (ExecutionHub.HasGroupSubscribers(group.Key.ExecutionId.ToString()))
                    sends.Add(SendGroupSafeAsync(ExecutionOnlyGroups(group.Key.ExecutionId), payload, ct));
            }
        }

        if (sends.Count > 0)
            await Task.WhenAll(sends);
    }

    private async Task SendGroupSafeAsync(string[] targets, LiveEventsBatch payload, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.Groups(targets).SendAsync("LiveEventsBatch", payload, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to flush {Count} live execution event(s).",
                payload.Events.Count);
        }
    }

    private async Task SendConnectionsSafeAsync(IReadOnlyList<string> connectionIds, LiveEventsBatch payload, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.Clients(connectionIds).SendAsync("LiveEventsBatch", payload, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to flush {Count} live-ops event(s) to {Count2} connection(s).",
                payload.Events.Count, connectionIds.Count);
        }
    }

    /// <summary>
    /// Resolves (and caches) the folder a workflow lives in, for the live-ops feed's per-event
    /// RBAC filter. Uses a fresh DI scope because the notifier is a singleton with no DbContext.
    /// Returns null when no scope factory is wired (unit tests inject a folder resolver instead).
    /// </summary>
    private async Task<Guid?> DefaultResolveFolderAsync(Guid workflowId, CancellationToken ct)
    {
        if (_workflowFolderCache.TryGetValue(workflowId, out var cached))
            return cached;
        if (_scopeFactory is null)
            return null;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        var folderId = await db.Workflows.AsNoTracking()
            .Where(w => w.Id == workflowId)
            .Select(w => (Guid?)w.FolderId)
            .FirstOrDefaultAsync(ct);
        if (folderId is not null)
            _workflowFolderCache[workflowId] = folderId.Value;
        return folderId;
    }

    private static (string? TraceId, string? SpanId) CurrentContext()
    {
        var current = Activity.Current;
        if (current is null) return (null, null);
        return (current.TraceId.ToString(), current.SpanId.ToString());
    }

    private sealed record QueuedLiveEvent(Guid ExecutionId, Guid WorkflowId, LiveEventBatchItem Item, bool IsStatusEvent = false);
}
