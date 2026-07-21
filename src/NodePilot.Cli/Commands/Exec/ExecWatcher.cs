using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using Spectre.Console;

namespace NodePilot.Cli.Commands.Exec;

/// <summary>
/// Live execution watcher. Tries SignalR first (group "executionId"); on connect failure
/// falls back to polling /api/executions/{id} and /steps every second. Exits when the
/// execution reaches a terminal status and translates the final status into the right
/// CLI exit code.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ExecWatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(
        NodePilotApiClient api,
        SessionContext session,
        Guid executionId,
        OutputWriter writer,
        bool followAfterTerminal,
        CancellationToken ct,
        bool forcePolling = false)
    {
        if (!forcePolling)
        {
            var rc = await TryStreamWithSignalRAsync(api, session, executionId, writer, ct);
            if (rc.HasValue) return rc.Value;
            writer.Warning("SignalR nicht erreichbar — falle auf Polling zurück.");
        }

        return await PollLoopAsync(api, executionId, writer, ct);
    }

    private static async Task<int?> TryStreamWithSignalRAsync(
        NodePilotApiClient api, SessionContext session, Guid executionId, OutputWriter writer, CancellationToken ct)
    {
        if (!session.HasServer || !session.HasSession) return null;
        var token = session.Session!.Token;
        var hubUrl = new Uri(new Uri(session.Server!.TrimEnd('/') + "/"), "hubs/execution");

        await using var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, opts => opts.AccessTokenProvider = () => Task.FromResult<string?>(token))
            .Build();

        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<JsonStepEvent>("StepStarted", e => HandleStepStarted(e, writer));
        connection.On<JsonStepCompletedEvent>("StepCompleted", e => HandleStepCompleted(e, writer));
        connection.On<JsonExecutionStatusEvent>("ExecutionStatusChanged", e => HandleExecutionStatus(e, writer, done));
        connection.On<JsonLiveEventsBatch>("LiveEventsBatch", batch =>
        {
            foreach (var item in batch.Events ?? [])
                HandleBatchItem(item, writer, done);
        });

        try
        {
            await connection.StartAsync(ct);
            await connection.InvokeAsync("JoinExecution", executionId.ToString(), ct);
        }
        catch (Exception ex)
        {
            writer.Warning($"SignalR-Connect fehlgeschlagen: {ex.Message}");
            return null;
        }

        // Catch-up: the run might already be terminal by the time we connected.
        var current = await api.GetExecutionAsync(executionId, ct);
        if (Commands.Workflow.WorkflowRunCommand.IsTerminal(current.Status))
        {
            writer.WriteData(current, (console, v) => Renderers.ExecutionDetail(console, v));
            return Commands.Workflow.WorkflowRunCommand.ExitCodeFor(current.Status);
        }

        using var reg = ct.Register(() => done.TrySetCanceled(ct));
        try
        {
            return await done.Task;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Error;
        }
    }

    internal static async Task<int> PollLoopAsync(NodePilotApiClient api, Guid executionId, OutputWriter writer, CancellationToken ct)
    {
        var seenSteps = new HashSet<Guid>();
        while (!ct.IsCancellationRequested)
        {
            var current = await api.GetExecutionAsync(executionId, ct);
            var steps = await api.GetStepsAsync(executionId, ct);
            foreach (var s in steps)
            {
                if (seenSteps.Add(s.Id))
                    writer.Info($"  {Renderers.StatusMarkup(s.Status)} {s.StepName ?? s.StepId} [silver]({s.StepType})[/]");
            }

            if (Commands.Workflow.WorkflowRunCommand.IsTerminal(current.Status))
            {
                writer.WriteData(current, (console, v) => Renderers.ExecutionDetail(console, v));
                return Commands.Workflow.WorkflowRunCommand.ExitCodeFor(current.Status);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return ExitCodes.Error;
    }

    private static void HandleBatchItem(
        JsonLiveEventItem item,
        OutputWriter writer,
        TaskCompletionSource<int> done)
    {
        if (item.Event.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;

        switch (item.Type)
        {
            case "StepStarted":
                if (item.Event.Deserialize<JsonStepEvent>(JsonOptions) is { } started)
                    HandleStepStarted(started, writer);
                break;
            case "StepCompleted":
                if (item.Event.Deserialize<JsonStepCompletedEvent>(JsonOptions) is { } completed)
                    HandleStepCompleted(completed, writer);
                break;
            case "ExecutionStatusChanged":
                if (item.Event.Deserialize<JsonExecutionStatusEvent>(JsonOptions) is { } status)
                    HandleExecutionStatus(status, writer, done);
                break;
        }
    }

    private static void HandleStepStarted(JsonStepEvent e, OutputWriter writer)
        => writer.Info($"[grey]→[/] {e.StepName ?? e.StepId} [silver]({e.StepType})[/]");

    private static void HandleStepCompleted(JsonStepCompletedEvent e, OutputWriter writer)
        => writer.Info($"  {Renderers.StatusMarkup(e.Status)} {e.StepName ?? e.StepId}");

    private static void HandleExecutionStatus(
        JsonExecutionStatusEvent e,
        OutputWriter writer,
        TaskCompletionSource<int> done)
    {
        if (!Commands.Workflow.WorkflowRunCommand.IsTerminal(e.Status))
            return;

        writer.Info($"[bold]Execution {e.Status}[/]");
        done.TrySetResult(Commands.Workflow.WorkflowRunCommand.ExitCodeFor(e.Status));
    }

    // Loose JSON wrappers — we don't import the Hub DTOs to keep the CLI free of API project refs.
    private sealed record JsonStepEvent(Guid ExecutionId, Guid WorkflowId, string StepId, string? StepName, string StepType, DateTime StartedAt);
    private sealed record JsonStepCompletedEvent(Guid ExecutionId, Guid WorkflowId, string StepId, string? StepName, string Status, string? Output, string? ErrorOutput, DateTime CompletedAt);
    private sealed record JsonExecutionStatusEvent(Guid ExecutionId, Guid WorkflowId, string Status, string? ErrorMessage, DateTime? CompletedAt);
    private sealed record JsonLiveEventsBatch(JsonLiveEventItem[]? Events);
    private sealed record JsonLiveEventItem(string Type, JsonElement Event);
}
