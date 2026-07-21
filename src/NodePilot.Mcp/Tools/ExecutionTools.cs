using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NodePilot.Mcp.Api;
using NodePilot.Mcp.Api.Dtos;
using NodePilot.Mcp.Mapping;

namespace NodePilot.Mcp.Tools;

/// <summary>
/// Execution read tools. Free-text fields (return data, stdout/stderr) are truncated to keep
/// payloads inside the agent's token budget. Control tools (execute/cancel/retry/resume) are
/// added in the execution-control phase.
/// </summary>
[McpServerToolType]
public sealed class ExecutionTools
{
    private readonly NodePilotApiClient _api;

    public ExecutionTools(NodePilotApiClient api) => _api = api;

    [McpServerTool(Name = "list_executions", ReadOnly = true)]
    [Description("List workflow executions (newest first) as slim summaries. Filter by workflowId, and/or only active (Running/Pending) or only terminal runs. Returns at most 'limit' rows (default 50).")]
    public async Task<object> ListExecutions(
        [Description("Optional workflow GUID to scope to one workflow.")] string? workflowId = null,
        [Description("When true, only Running/Pending executions.")] bool activeOnly = false,
        [Description("When true, only terminal (Succeeded/Failed/Cancelled) executions.")] bool terminalOnly = false,
        [Description("Maximum rows to return (1-200, default 50).")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        Guid? wf = null;
        if (!string.IsNullOrWhiteSpace(workflowId))
        {
            if (!Guid.TryParse(workflowId, out var parsed))
                throw new McpException("workflowId must be a GUID. Use list_workflows or get_workflow to find it.");
            wf = parsed;
        }

        var all = await ApiErrorMapper.Guard(() => _api.ListExecutionsAsync(wf, activeOnly, terminalOnly, cancellationToken));
        var rows = all.Take(Math.Clamp(limit, 1, 200)).Select(e => new
        {
            id = e.Id,
            workflowId = e.WorkflowId,
            status = e.Status,
            startedAt = e.StartedAt,
            completedAt = e.CompletedAt,
            triggeredBy = e.TriggeredBy,
            stepsCompleted = e.StepsCompleted,
            stepsTotal = e.StepsTotal,
            failedSteps = e.FailedSteps?.Select(f => f.StepName ?? f.StepId),
        }).ToList();
        return new { count = rows.Count, totalAvailable = all.Count, executions = rows };
    }

    [McpServerTool(Name = "get_execution", ReadOnly = true)]
    [Description("Get one execution by id, including status, error message and return data (truncated if large).")]
    public async Task<object> GetExecution(
        [Description("The execution GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var guid = ParseGuid(id, "execution id");
        var e = await ApiErrorMapper.Guard(() => _api.GetExecutionAsync(guid, cancellationToken));
        return new
        {
            e.Id,
            e.WorkflowId,
            e.Status,
            e.StartedAt,
            e.CompletedAt,
            e.TriggeredBy,
            e.StartedByUsername,
            errorMessage = PayloadShaping.Truncate(e.ErrorMessage),
            returnData = PayloadShaping.Truncate(e.ReturnData),
            e.ParentExecutionId,
            e.ParentWorkflowName,
            e.TraceId,
        };
    }

    [McpServerTool(Name = "get_execution_steps", ReadOnly = true)]
    [Description("Get the per-step results of an execution: status, target machine, attempt count and (truncated) stdout/stderr. Use this to debug why a run failed.")]
    public async Task<object> GetExecutionSteps(
        [Description("The execution GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var guid = ParseGuid(id, "execution id");
        var steps = await ApiErrorMapper.Guard(() => _api.GetStepsAsync(guid, cancellationToken));
        var rows = steps.Select(s => new
        {
            stepId = s.StepId,
            stepName = s.StepName,
            stepType = s.StepType,
            targetMachine = s.TargetMachine,
            status = s.Status,
            startedAt = s.StartedAt,
            completedAt = s.CompletedAt,
            attemptCount = s.AttemptCount,
            output = PayloadShaping.Truncate(s.Output),
            errorOutput = PayloadShaping.Truncate(s.ErrorOutput),
            outputVariable = s.OutputVariable,
        });
        return new { executionId = guid, steps = rows };
    }

    // ---- Control ------------------------------------------------------------

    [McpServerTool(Name = "execute_workflow")]
    [Description("Start a workflow run (Admin/Operator). Returns 202 with the new ExecutionId; progress is asynchronous — poll get_execution / get_execution_steps. Parameters land as manual.* variables in the run.")]
    public async Task<object> ExecuteWorkflow(
        [Description("The workflow GUID.")] string workflowId,
        [Description("Optional input parameters (string key/value) passed to the run as manual.* variables.")] Dictionary<string, string>? parameters = null,
        [Description("Optional overall run timeout in seconds (caps the whole run, not per-step).")] int? timeoutSeconds = null,
        [Description("When true, start in debug mode (breakpoints; resume via resume_execution).")] bool debug = false,
        CancellationToken cancellationToken = default)
    {
        var id = ParseGuid(workflowId, "workflowId");
        var req = new ExecuteWorkflowRequest(parameters, timeoutSeconds, debug);
        var e = await ApiErrorMapper.Guard(() => _api.ExecuteWorkflowAsync(id, req, cancellationToken));
        return new { executionId = e.Id, status = e.Status, startedAt = e.StartedAt };
    }

    [McpServerTool(Name = "cancel_execution", Idempotent = true)]
    [Description("Cancel a single running execution (Admin/Operator). Idempotent if already terminal.")]
    public async Task<object> CancelExecution(
        [Description("The execution GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var guid = ParseGuid(id, "execution id");
        await ApiErrorMapper.Guard(() => _api.CancelExecutionAsync(guid, cancellationToken));
        return new { cancelled = true, executionId = guid };
    }

    [McpServerTool(Name = "retry_execution")]
    [Description("Retry a terminal execution with the same input parameters (Admin/Operator). Returns the new ExecutionId.")]
    public async Task<object> RetryExecution(
        [Description("The execution GUID to retry.")] string id,
        CancellationToken cancellationToken = default)
    {
        var guid = ParseGuid(id, "execution id");
        var e = await ApiErrorMapper.Guard(() => _api.RetryExecutionAsync(guid, cancellationToken));
        return new { executionId = e.Id, status = e.Status };
    }

    [McpServerTool(Name = "list_paused_steps", ReadOnly = true)]
    [Description("List the step ids currently paused at a breakpoint for a debug execution. Call this to get the stepId you must pass to resume_execution (parallel branches each have their own paused step).")]
    public async Task<object> ListPausedSteps(
        [Description("The execution GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var guid = ParseGuid(id, "execution id");
        var steps = await ApiErrorMapper.Guard(() => _api.GetPausedStepsAsync(guid, cancellationToken));
        return new { executionId = guid, pausedStepIds = steps };
    }

    [McpServerTool(Name = "resume_execution")]
    [Description("Resume a paused (debug) execution (Admin/Operator). REQUIRES the stepId of the paused step (get it from list_paused_steps). mode: 'continue' runs on, 'stepOver' advances one step, 'stop' ends the run. Optional variable overrides are applied before resuming.")]
    public async Task<object> ResumeExecution(
        [Description("The execution GUID.")] string id,
        [Description("The paused step id to resume (from list_paused_steps). REQUIRED.")] string stepId,
        [Description("Resume mode: continue | stepOver | stop.")] string mode,
        [Description("Optional variable overrides to apply before resuming.")] Dictionary<string, string>? overrides = null,
        CancellationToken cancellationToken = default)
    {
        var guid = ParseGuid(id, "execution id");
        if (string.IsNullOrWhiteSpace(stepId))
            throw new McpException("stepId is required. Use list_paused_steps to find the paused step id.");
        var allowed = new[] { "continue", "stepOver", "stop" };
        if (!allowed.Contains(mode, StringComparer.Ordinal))
            throw new McpException($"mode must be one of: {string.Join(", ", allowed)}.");
        await ApiErrorMapper.Guard(() => _api.ResumeExecutionAsync(guid, new ResumeExecutionRequest(stepId, mode, overrides), cancellationToken));
        return new { resumed = true, executionId = guid, stepId, mode };
    }

    [McpServerTool(Name = "trigger_external_workflow")]
    [Description("Trigger a workflow via the external API-key endpoint (POST /api/trigger/{nameOrId}). Supply the X-Api-Key value. Supports an idempotencyKey (24h replay window). Returns the ExecutionId and whether an idempotent replay occurred.")]
    public async Task<object> TriggerExternalWorkflow(
        [Description("The workflow name or GUID.")] string workflowNameOrId,
        [Description("The external-trigger API key (sent as X-Api-Key).")] string apiKey,
        [Description("Optional input parameters passed to the run.")] Dictionary<string, string>? parameters = null,
        [Description("Optional Idempotency-Key; a repeat within 24h replays the original run instead of starting a new one.")] string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var req = new ExecuteWorkflowRequest(parameters);
        var (execution, replayed) = await ApiErrorMapper.Guard(
            () => _api.TriggerExternalAsync(workflowNameOrId, apiKey, req, idempotencyKey, cancellationToken));
        return new { executionId = execution.Id, status = execution.Status, idempotentReplayed = replayed };
    }

    internal static Guid ParseGuid(string value, string label)
        => Guid.TryParse(value, out var g) ? g : throw new McpException($"{label} must be a GUID, got '{value}'.");
}
