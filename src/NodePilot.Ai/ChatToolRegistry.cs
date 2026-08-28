using System.Text.Json;
using NodePilot.Core.Activities;
using NodePilot.Core.Interfaces;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Ai;

/// <summary>Context a chat tool executes in: the (redacted) definition, an optional workflow ID,
/// and an optional execution-log reader. <see cref="ExecutionLogs"/> is only set once the
/// controller has verified folder-read access for the workflow ID — without a reader, the
/// execution tools are neither offered nor executed.</summary>
public sealed record ChatToolContext(
    JsonElement WorkflowDefinition,
    Guid? WorkflowId,
    IExecutionLogReader? ExecutionLogs = null);

/// <summary>
/// Provides the AI chat assistant with a curated set of read-only tools (function calling). Under
/// <c>tool_choice: auto</c>, the LLM decides whether to call one. The tools cover in-process
/// analysis of the definition plus execution-log reads through the reader carried in the context;
/// the registry itself stays a stateless singleton.
/// </summary>
public interface IChatToolRegistry
{
    /// <summary>The tool definitions offered to the LLM (name/description/parameter schema).
    /// Execution tools are only offered when the context carries a reader.</summary>
    IReadOnlyList<LlmToolDefinition> GetTools(ChatToolContext context);

    /// <summary>Executes a tool and returns the result as a JSON string (for the tool-role turn).
    /// Unknown tools / errors come back as <c>{ "error": … }</c> instead of aborting the loop.</summary>
    Task<string> ExecuteAsync(string name, string argumentsJson, ChatToolContext context, CancellationToken ct);
}

/// <summary>
/// Read-only tool registry: <c>analyze_workflow</c> (static review) + <c>list_activity_types</c>
/// (grounding) — both purely deterministic, backed by <see cref="NodePilot.Core"/> — plus the
/// execution-log tools <c>list_recent_executions</c> / <c>get_execution_steps</c> /
/// <c>get_failure_context</c>, which read via the (already-authorized)
/// <see cref="IExecutionLogReader"/> carried in the context. Their output is redacted by the
/// reader itself and additionally truncated here (token budget).
/// </summary>
public sealed class WorkflowChatToolRegistry : IChatToolRegistry
{
    private static readonly JsonElement NoParams =
        ChatToolDispatch.ParseParams("""{"type":"object","properties":{}}""");
    private static readonly JsonSerializerOptions Json = new();

    // Token-budget caps. The reader redacts the full string and truncation happens only here, so
    // no secret can survive a truncate-then-redact order. The caps stay below the MCP server's
    // because the chat prompt already carries the workflow JSON.
    private const int MaxOutputChars = 1_500;
    private const int MaxErrorMessageChars = 500;
    private const int FailureContextOutputChars = 2_000;
    private const int MaxStepsPerExecution = 100;
    private const int DefaultTake = 10;
    private const int MaxTake = 20;

    private static readonly HashSet<string> ExecutionToolNames = new(StringComparer.Ordinal)
    {
        "list_recent_executions", "get_execution_steps", "get_failure_context",
    };

    private readonly Dictionary<string, (LlmToolDefinition Def, ChatToolHandler<ChatToolContext> Handler)> _tools;

    public WorkflowChatToolRegistry()
    {
        _tools = new Dictionary<string, (LlmToolDefinition, ChatToolHandler<ChatToolContext>)>(StringComparer.Ordinal)
        {
            ["analyze_workflow"] = (
                new LlmToolDefinition("analyze_workflow",
                    "Deterministische Static-Analyse des aktuell geöffneten Workflows: findet fehlenden Trigger, "
                    + "unerreichbare (Orphan-)Steps, Zyklen, Remote-Steps ohne Ziel-Maschine und Struktur-Fehler. "
                    + "Rufe es bei 'prüfe/review den Workflow' oder BEVOR du strukturelle Probleme behauptest - es ist "
                    + "verlaesslicher als blosses Lesen des JSON.",
                    NoParams),
                (_, ctx, _) => Task.FromResult(AnalyzeWorkflow(ctx))),

            ["list_activity_types"] = (
                new LlmToolDefinition("list_activity_types",
                    "Listet alle verfügbaren Activity-/Trigger-Typen mit Kategorie, remote-Flag und Output-Parametern. "
                    + "Rufe es nur, wenn du sicher sein musst, welche Activity-Typen + Outputs existieren.",
                    NoParams),
                (_, _, _) => Task.FromResult(ListActivityTypes())),

            ["list_recent_executions"] = (
                new LlmToolDefinition("list_recent_executions",
                    "Listet die jüngsten Ausführungen (Runs) des aktuell geöffneten Workflows: Status, Zeiten, "
                    + "Fehlermeldung und fehlgeschlagene Steps. Rufe es, wenn der User nach vergangenen Läufen oder "
                    + "Fehlschlägen fragt. Ergebnisse sind redigiert und gekürzt.",
                    ChatToolDispatch.ParseParams("""
                        {"type":"object","properties":{"take":{"type":"integer","minimum":1,"maximum":20,
                        "description":"Anzahl der jüngsten Läufe (Default 10)."}}}
                        """)),
                ListRecentExecutionsAsync),

            ["get_execution_steps"] = (
                new LlmToolDefinition("get_execution_steps",
                    "Liefert die Step-Details EINER Ausführung dieses Workflows: pro Step Status, Versuche "
                    + "(attemptCount), Output und ErrorOutput (redigiert + gekürzt). executionId stammt aus "
                    + "list_recent_executions.",
                    ChatToolDispatch.ParseParams("""
                        {"type":"object","properties":{"executionId":{"type":"string",
                        "description":"GUID der Execution (aus list_recent_executions)."}},"required":["executionId"]}
                        """)),
                GetExecutionStepsAsync),

            ["get_failure_context"] = (
                new LlmToolDefinition("get_failure_context",
                    "One-Call-Debugging: durchsucht die jüngsten 20 Läufe dieses Workflows nach dem neuesten "
                    + "FEHLGESCHLAGENEN und liefert dessen fehlgeschlagene Steps mit ErrorOutput. Ein älterer "
                    + "Fehlschlag (jenseits der 20 jüngsten Läufe) ist hier nicht sichtbar — nutze dann "
                    + "list_recent_executions mit größerem take (max 20) oder get_execution_steps gezielt. "
                    + "Rufe es zuerst bei 'warum ist der Workflow fehlgeschlagen?'.",
                    NoParams),
                GetFailureContextAsync),
        };
    }

    public IReadOnlyList<LlmToolDefinition> GetTools(ChatToolContext context) =>
        _tools.Values
            .Where(t => context.ExecutionLogs is not null || !ExecutionToolNames.Contains(t.Def.Name))
            .Select(t => t.Def)
            .ToList();

    public async Task<string> ExecuteAsync(string name, string argumentsJson, ChatToolContext context, CancellationToken ct)
    {
        if (!_tools.TryGetValue(name, out var tool))
            return ChatToolDispatch.UnknownTool(name, Json);
        return await ChatToolDispatch.ExecuteAsync(tool.Handler, argumentsJson, NoParams, context, Json, ct);
    }

    private static object AnalyzeWorkflow(ChatToolContext ctx)
    {
        var result = WorkflowAnalyzer.Analyze(ctx.WorkflowDefinition);
        return new
        {
            ok = result.Ok,
            count = result.Findings.Count,
            nodeCount = result.NodeCount,
            edgeCount = result.EdgeCount,
            roots = result.Roots,
            findings = result.Findings.Select(f => new
            {
                severity = f.Severity,
                code = f.Code,
                message = f.Message,
                nodeId = f.NodeId,
            }).ToArray(),
        };
    }

    private static object ListActivityTypes() => new
    {
        activities = ActivityCatalog.All.Select(a => new
        {
            type = a.Type,
            category = a.Category.ToString(),
            isTrigger = a.IsTrigger,
            isRemote = a.IsRemote,
            outputs = a.OutputParameters.Select(o => o.Name).ToArray(),
        }).ToArray(),
    };

    // ---- Execution log tools -------------------------------------------------------------------

    /// <summary>Defense-in-depth guard for an execution tool called without a reader: a tool that
    /// is not offered to the model can still be named by it.</summary>
    private static bool TryGetExecutionScope(ChatToolContext ctx, out IExecutionLogReader logs, out Guid workflowId)
    {
        if (ctx.ExecutionLogs is { } reader && ctx.WorkflowId is { } id && id != Guid.Empty)
        {
            logs = reader;
            workflowId = id;
            return true;
        }
        logs = null!;
        workflowId = Guid.Empty;
        return false;
    }

    private static readonly object ExecutionToolsUnavailable = new
    {
        error = "Ausführungs-Historie ist hier nicht verfügbar (Workflow nicht gespeichert oder keine Berechtigung).",
    };

    private static async Task<object> ListRecentExecutionsAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        if (!TryGetExecutionScope(ctx, out var logs, out var workflowId))
            return ExecutionToolsUnavailable;

        var take = DefaultTake;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("take", out var takeEl))
        {
            if (takeEl.ValueKind == JsonValueKind.Number && takeEl.TryGetInt32(out var n)) take = n;
            else if (takeEl.ValueKind == JsonValueKind.String && int.TryParse(takeEl.GetString(), out var s)) take = s;
        }
        take = Math.Clamp(take, 1, MaxTake);

        var executions = await logs.GetRecentExecutionsAsync(workflowId, take, ct);
        return new
        {
            workflowId,
            count = executions.Count,
            executions = executions.Select(ToExecutionRow).ToArray(),
        };
    }

    private static async Task<object> GetExecutionStepsAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        if (!TryGetExecutionScope(ctx, out var logs, out var workflowId))
            return ExecutionToolsUnavailable;

        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("executionId", out var idEl)
            || idEl.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idEl.GetString(), out var executionId))
            return new { error = "executionId ist keine gültige GUID." };

        var result = await logs.GetExecutionStepsAsync(workflowId, executionId, ct);
        if (result is null)
            return new { error = "Execution nicht gefunden oder gehört nicht zu diesem Workflow." };

        return new
        {
            execution = ToExecutionRow(result.Execution),
            steps = result.Steps.Take(MaxStepsPerExecution).Select(s => ToStepRow(s, MaxOutputChars)).ToArray(),
            truncatedSteps = result.Steps.Count > MaxStepsPerExecution,
        };
    }

    private static async Task<object> GetFailureContextAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        if (!TryGetExecutionScope(ctx, out var logs, out var workflowId))
            return ExecutionToolsUnavailable;

        var recent = await logs.GetRecentExecutionsAsync(workflowId, MaxTake, ct);
        var failed = recent.FirstOrDefault(e => string.Equals(e.Status, "Failed", StringComparison.Ordinal));
        if (failed is null)
            return new { message = $"Keine fehlgeschlagenen Ausführungen in den letzten {recent.Count} Läufen gefunden." };

        var details = await logs.GetExecutionStepsAsync(workflowId, failed.Id, ct);
        if (details is null) // Race: retention deleted the run between the two reads.
            return new { error = "Execution nicht gefunden oder gehört nicht zu diesem Workflow." };

        return new
        {
            execution = ToExecutionRow(details.Execution),
            failingSteps = details.Steps
                .Where(s => string.Equals(s.Status, "Failed", StringComparison.Ordinal))
                .Take(MaxStepsPerExecution)
                .Select(s => ToStepRow(s, FailureContextOutputChars))
                .ToArray(),
        };
    }

    private static object ToExecutionRow(ExecutionLogSummary e) => new
    {
        id = e.Id,
        status = e.Status,
        startedAt = e.StartedAt,
        completedAt = e.CompletedAt,
        durationMs = e.CompletedAt is { } done ? (long?)(done - e.StartedAt).TotalMilliseconds : null,
        triggeredBy = e.TriggeredBy,
        errorMessage = Truncate(e.ErrorMessage, MaxErrorMessageChars),
        stepsTotal = e.StepsTotal,
        failedSteps = e.FailedSteps.Select(f => new { stepId = f.StepId, stepName = f.StepName }).ToArray(),
    };

    private static object ToStepRow(StepExecutionLog s, int outputCap) => new
    {
        stepId = s.StepId,
        stepName = s.StepName,
        stepType = s.StepType,
        targetMachine = s.TargetMachine,
        status = s.Status,
        startedAt = s.StartedAt,
        completedAt = s.CompletedAt,
        attemptCount = s.AttemptCount,
        output = Truncate(s.Output, outputCap),
        errorOutput = Truncate(s.ErrorOutput, outputCap),
    };

    private static string? Truncate(string? value, int maxChars)
    {
        if (value is null || value.Length <= maxChars) return value;
        return value[..maxChars] + $"…[+{value.Length - maxChars} Zeichen abgeschnitten]";
    }

}
