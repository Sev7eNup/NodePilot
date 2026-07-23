using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NodePilot.Mcp.Api.Dtos;
using NodePilot.Mcp.Config;

namespace NodePilot.Mcp.Api;

/// <summary>
/// Typed HttpClient wrapper around the NodePilot REST API — one method per endpoint, every
/// non-2xx becomes <see cref="ApiException"/>. Copied/adapted from the CLI's client and grown
/// per phase. Authentication is a Bearer header (accepted alongside the SPA's httpOnly cookie).
/// </summary>
public sealed class NodePilotApiClient
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly HttpClient _http;

    public NodePilotApiClient(HttpClient http) => _http = http;

    public HttpClient Http => _http;

    /// <summary>Resolved connection context (server/profile/token state) for diagnostics + guards.</summary>
    public SessionContext? Session { get; init; }

    public string? BearerToken
    {
        get => _http.DefaultRequestHeaders.Authorization?.Parameter;
        set => _http.DefaultRequestHeaders.Authorization = string.IsNullOrEmpty(value)
            ? null
            : new AuthenticationHeaderValue("Bearer", value);
    }

    public Uri? BaseAddress
    {
        get => _http.BaseAddress;
        set => _http.BaseAddress = value;
    }

    private void EnsureReady()
    {
        if (_http.BaseAddress is null)
            throw new NotConfiguredException(
                "No NodePilot server is configured. Set NODEPILOT_MCP_SERVER (or a CLI profile via `np config set server <URL>`).");
    }

    // ---- Auth ---------------------------------------------------------------

    public async Task<MeResponse> MeAsync(CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync("api/auth/me", ct);
        return await ParseAsync<MeResponse>(res, ct);
    }

    // ---- Workflows (read) ---------------------------------------------------

    public async Task<List<WorkflowResponse>> ListWorkflowsAsync(CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync("api/workflows", ct);
        return await ParseAsync<List<WorkflowResponse>>(res, ct);
    }

    public async Task<WorkflowResponse> GetWorkflowAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync($"api/workflows/{id}", ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task<WorkflowResponse> GetWorkflowByNameAsync(string name, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync($"api/workflows/by-name/{Uri.EscapeDataString(name)}", ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task<WorkflowContractResponse> GetContractAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync($"api/workflows/{id}/contract", ct);
        return await ParseAsync<WorkflowContractResponse>(res, ct);
    }

    public async Task<WorkflowContractResponse> GetContractByNameAsync(string name, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync($"api/workflows/by-name/{Uri.EscapeDataString(name)}/contract", ct);
        return await ParseAsync<WorkflowContractResponse>(res, ct);
    }

    public async Task<List<WorkflowVersionInfo>> ListVersionsAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync($"api/workflows/{id}/versions", ct);
        return await ParseAsync<List<WorkflowVersionInfo>>(res, ct);
    }

    public async Task<WorkflowVersionDetail> GetVersionAsync(Guid id, int version, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync($"api/workflows/{id}/versions/{version}", ct);
        return await ParseAsync<WorkflowVersionDetail>(res, ct);
    }

    public async Task<WorkflowExportEnvelope> ExportOneAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync($"api/workflows/{id}/export", ct);
        return await ParseAsync<WorkflowExportEnvelope>(res, ct);
    }

    // ---- Workflows (write / lifecycle) --------------------------------------

    public async Task<WorkflowResponse> CreateWorkflowAsync(CreateWorkflowRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsJsonAsync("api/workflows", req, JsonOptions, ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task UpdateWorkflowAsync(Guid id, UpdateWorkflowRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PutAsJsonAsync($"api/workflows/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteWorkflowAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.DeleteAsync($"api/workflows/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<WorkflowResponse> DuplicateWorkflowAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsync($"api/workflows/{id}/duplicate", content: null, ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task EnableWorkflowAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsync($"api/workflows/{id}/enable", content: null, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DisableWorkflowAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsync($"api/workflows/{id}/disable", content: null, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<WorkflowResponse> LockWorkflowAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsync($"api/workflows/{id}/lock", content: null, ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task<WorkflowResponse> UnlockWorkflowAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsync($"api/workflows/{id}/unlock", content: null, ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task<WorkflowResponse> ForceUnlockWorkflowAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsync($"api/workflows/{id}/force-unlock", content: null, ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task<WorkflowResponse> PublishWorkflowAsync(Guid id, PublishWorkflowRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsJsonAsync($"api/workflows/{id}/publish", req, JsonOptions, ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task<WorkflowResponse> RollbackAsync(Guid id, int version, RollbackRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsJsonAsync($"api/workflows/{id}/rollback/{version}", req, JsonOptions, ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task<ImportWorkflowsResponse> ImportAsync(WorkflowExportEnvelope envelope, Guid? folderId, CancellationToken ct)
    {
        EnsureReady();
        var path = folderId is null ? "api/workflows/import" : $"api/workflows/import?folderId={folderId}";
        using var res = await _http.PostAsJsonAsync(path, envelope, JsonOptions, ct);
        return await ParseAsync<ImportWorkflowsResponse>(res, ct);
    }

    public async Task<JsonElement> ImportScorchAsync(string xml, Guid? folderId, CancellationToken ct)
    {
        EnsureReady();
        var path = folderId is null ? "api/workflows/import-scorch" : $"api/workflows/import-scorch?folderId={folderId}";
        using var content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml");
        using var res = await _http.PostAsync(path, content, ct);
        return await ParseAsync<JsonElement>(res, ct);
    }

    // ---- Step test ----------------------------------------------------------

    public async Task<StepTestResponse> TestStepAsync(Guid workflowId, string stepId, StepTestRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsJsonAsync(
            $"api/workflows/{workflowId}/steps/{Uri.EscapeDataString(stepId)}/test", req, JsonOptions, ct);
        return await ParseAsync<StepTestResponse>(res, ct);
    }

    public async Task<StepTestContextResponse> GetStepTestContextAsync(Guid workflowId, string stepId, Guid? executionId, CancellationToken ct)
    {
        EnsureReady();
        var path = $"api/workflows/{workflowId}/steps/{Uri.EscapeDataString(stepId)}/test-context"
                 + (executionId.HasValue ? $"?executionId={executionId.Value}" : "");
        using var res = await _http.GetAsync(path, ct);
        return await ParseAsync<StepTestContextResponse>(res, ct);
    }

    public async Task<List<StepTestContextRunInfo>> ListStepTestRunsAsync(Guid workflowId, string stepId, int? limit, CancellationToken ct)
    {
        EnsureReady();
        var path = $"api/workflows/{workflowId}/steps/{Uri.EscapeDataString(stepId)}/test-context/runs"
                 + (limit.HasValue ? $"?limit={limit.Value}" : "");
        using var res = await _http.GetAsync(path, ct);
        return await ParseAsync<List<StepTestContextRunInfo>>(res, ct);
    }

    // ---- Executions (read) --------------------------------------------------

    public async Task<List<ExecutionResponse>> ListExecutionsAsync(
        Guid? workflowId, bool? activeOnly, bool? terminalOnly, CancellationToken ct)
    {
        EnsureReady();
        var q = new List<string>();
        if (workflowId is { } w) q.Add($"workflowId={w}");
        if (activeOnly == true) q.Add("activeOnly=true");
        if (terminalOnly == true) q.Add("terminalOnly=true");
        var url = "api/executions" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
        using var res = await _http.GetAsync(url, ct);
        return await ParseAsync<List<ExecutionResponse>>(res, ct);
    }

    public async Task<ExecutionResponse> GetExecutionAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync($"api/executions/{id}", ct);
        return await ParseAsync<ExecutionResponse>(res, ct);
    }

    public async Task<List<StepExecutionResponse>> GetStepsAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync($"api/executions/{id}/steps", ct);
        return await ParseAsync<List<StepExecutionResponse>>(res, ct);
    }

    // ---- Executions (control) -----------------------------------------------

    public async Task<ExecutionResponse> ExecuteWorkflowAsync(Guid id, ExecuteWorkflowRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsJsonAsync($"api/workflows/{id}/execute", req, JsonOptions, ct);
        return await ParseAsync<ExecutionResponse>(res, ct);
    }

    public async Task CancelExecutionAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsync($"api/executions/{id}/cancel", content: null, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<ExecutionResponse> RetryExecutionAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsync($"api/executions/{id}/retry", content: null, ct);
        return await ParseAsync<ExecutionResponse>(res, ct);
    }

    public async Task ResumeExecutionAsync(Guid id, ResumeExecutionRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsJsonAsync($"api/executions/{id}/resume", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<List<string>> GetPausedStepsAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync($"api/executions/{id}/paused-steps", ct);
        return await ParseAsync<List<string>>(res, ct);
    }

    public async Task<CancelAllResponse> CancelAllAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsync($"api/workflows/{id}/cancel-all", content: null, ct);
        return await ParseAsync<CancelAllResponse>(res, ct);
    }

    /// <summary>External trigger (X-Api-Key). Returns the execution and whether an Idempotency-Key replay occurred.</summary>
    public async Task<(ExecutionResponse Execution, bool IdempotentReplayed)> TriggerExternalAsync(
        string workflowNameOrId, string apiKey, ExecuteWorkflowRequest req, string? idempotencyKey, CancellationToken ct)
    {
        EnsureReady();
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"api/trigger/{Uri.EscapeDataString(workflowNameOrId)}")
        {
            Content = JsonContent.Create(req, options: JsonOptions),
        };
        msg.Headers.Add("X-Api-Key", apiKey);
        if (!string.IsNullOrWhiteSpace(idempotencyKey)) msg.Headers.Add("Idempotency-Key", idempotencyKey);

        using var res = await _http.SendAsync(msg, ct);
        var replayed = res.Headers.TryGetValues("Idempotent-Replayed", out var vals)
            && vals.Any(v => v.Equals("true", StringComparison.OrdinalIgnoreCase));
        var execution = await ParseAsync<ExecutionResponse>(res, ct);
        return (execution, replayed);
    }

    // ---- Scheduler ----------------------------------------------------------

    public async Task<NextFiresResponse> CronNextFiresAsync(string cron, int count, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync(
            $"api/triggers/schedule/next-fires?cron={Uri.EscapeDataString(cron)}&count={count}", ct);
        return await ParseAsync<NextFiresResponse>(res, ct);
    }

    // ---- Telemetry ----------------------------------------------------------

    public async Task<JsonElement> GetDashboardAsync(CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync("api/stats/dashboard", ct);
        return await ParseAsync<JsonElement>(res, ct);
    }

    public async Task<JsonElement> GetOperationsGraphAsync(CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync("api/operations/graph", ct);
        return await ParseAsync<JsonElement>(res, ct);
    }

    public async Task<WorkflowCoverageResponse> GetCoverageAsync(Guid id, int? windowDays, CancellationToken ct)
    {
        EnsureReady();
        var url = $"api/workflows/{id}/coverage" + (windowDays.HasValue ? $"?windowDays={windowDays.Value}" : "");
        using var res = await _http.GetAsync(url, ct);
        return await ParseAsync<WorkflowCoverageResponse>(res, ct);
    }

    public async Task<Dictionary<string, List<StepHealthEntry>>> GetStepHealthAsync(Guid id, string? stepIds, int? limit, CancellationToken ct)
    {
        EnsureReady();
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(stepIds)) q.Add($"stepIds={Uri.EscapeDataString(stepIds)}");
        if (limit.HasValue) q.Add($"limit={limit.Value}");
        var url = $"api/workflows/{id}/step-health" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
        using var res = await _http.GetAsync(url, ct);
        return await ParseAsync<Dictionary<string, List<StepHealthEntry>>>(res, ct);
    }

    public async Task<Dictionary<string, StepStats>> GetStepStatsAsync(Guid id, int? windowDays, CancellationToken ct)
    {
        EnsureReady();
        var url = $"api/workflows/{id}/step-stats" + (windowDays.HasValue ? $"?windowDays={windowDays.Value}" : "");
        using var res = await _http.GetAsync(url, ct);
        return await ParseAsync<Dictionary<string, StepStats>>(res, ct);
    }

    public async Task<AuditPageResponse> AuditAsync(
        string? action, string? resourceType, string? afterTs, string? afterId, string? since, string? until, int? take, CancellationToken ct)
    {
        EnsureReady();
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(action)) q.Add($"action={Uri.EscapeDataString(action)}");
        if (!string.IsNullOrWhiteSpace(resourceType)) q.Add($"resourceType={Uri.EscapeDataString(resourceType)}");
        if (!string.IsNullOrWhiteSpace(since)) q.Add($"since={Uri.EscapeDataString(since)}");
        if (!string.IsNullOrWhiteSpace(until)) q.Add($"until={Uri.EscapeDataString(until)}");
        if (!string.IsNullOrWhiteSpace(afterTs)) q.Add($"afterTs={Uri.EscapeDataString(afterTs)}");
        if (!string.IsNullOrWhiteSpace(afterId)) q.Add($"afterId={Uri.EscapeDataString(afterId)}");
        if (take.HasValue) q.Add($"take={take.Value}");
        var url = "api/audit" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
        using var res = await _http.GetAsync(url, ct);
        return await ParseAsync<AuditPageResponse>(res, ct);
    }

    public async Task<JsonElement> GetSupportLogAsync(int? lines, CancellationToken ct)
    {
        EnsureReady();
        var url = "api/diagnostics/support-log" + (lines.HasValue ? $"?lines={lines.Value}" : "");
        using var res = await _http.GetAsync(url, ct);
        return await ParseAsync<JsonElement>(res, ct);
    }

    public async Task<JsonElement> GetSupportEventsAsync(int? take, CancellationToken ct)
    {
        EnsureReady();
        var url = "api/diagnostics/support-events" + (take.HasValue ? $"?take={take.Value}" : "");
        using var res = await _http.GetAsync(url, ct);
        return await ParseAsync<JsonElement>(res, ct);
    }

    // ---- Machines -----------------------------------------------------------

    public async Task<List<MachineResponse>> ListMachinesAsync(CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync("api/machines", ct);
        return await ParseAsync<List<MachineResponse>>(res, ct);
    }

    public async Task<MachineResponse> GetMachineAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync($"api/machines/{id}", ct);
        return await ParseAsync<MachineResponse>(res, ct);
    }

    public async Task<MachineResponse> CreateMachineAsync(CreateMachineRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsJsonAsync("api/machines", req, JsonOptions, ct);
        return await ParseAsync<MachineResponse>(res, ct);
    }

    public async Task UpdateMachineAsync(Guid id, UpdateMachineRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PutAsJsonAsync($"api/machines/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteMachineAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.DeleteAsync($"api/machines/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<TestConnectionResponse> TestMachineAsync(Guid id, TestConnectionRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsJsonAsync($"api/machines/{id}/test", req, JsonOptions, ct);
        return await ParseAsync<TestConnectionResponse>(res, ct);
    }

    // ---- Credentials --------------------------------------------------------

    public async Task<List<CredentialResponse>> ListCredentialsAsync(CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync("api/credentials", ct);
        return await ParseAsync<List<CredentialResponse>>(res, ct);
    }

    public async Task<CredentialResponse> GetCredentialAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync($"api/credentials/{id}", ct);
        return await ParseAsync<CredentialResponse>(res, ct);
    }

    public async Task<CredentialResponse> CreateCredentialAsync(CreateCredentialRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsJsonAsync("api/credentials", req, JsonOptions, ct);
        return await ParseAsync<CredentialResponse>(res, ct);
    }

    public async Task UpdateCredentialAsync(Guid id, UpdateCredentialRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PutAsJsonAsync($"api/credentials/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteCredentialAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.DeleteAsync($"api/credentials/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    // ---- Global variables ---------------------------------------------------

    public async Task<List<GlobalVariableResponse>> ListGlobalsAsync(CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync("api/global-variables", ct);
        return await ParseAsync<List<GlobalVariableResponse>>(res, ct);
    }

    public async Task<GlobalVariableResponse> CreateGlobalAsync(CreateGlobalVariableRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsJsonAsync("api/global-variables", req, JsonOptions, ct);
        return await ParseAsync<GlobalVariableResponse>(res, ct);
    }

    public async Task UpdateGlobalAsync(Guid id, UpdateGlobalVariableRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PutAsJsonAsync($"api/global-variables/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteGlobalAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.DeleteAsync($"api/global-variables/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task MoveGlobalToFolderAsync(Guid id, Guid folderId, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsJsonAsync($"api/global-variables/{id}/move-folder", new MoveGlobalVariableRequest(folderId), JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    // ---- Global variable folders --------------------------------------------

    public async Task<List<GlobalVariableFolderResponse>> ListGlobalFoldersAsync(CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync("api/global-variable-folders", ct);
        return await ParseAsync<List<GlobalVariableFolderResponse>>(res, ct);
    }

    public async Task<GlobalVariableFolderResponse> CreateGlobalFolderAsync(CreateGlobalVariableFolderRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsJsonAsync("api/global-variable-folders", req, JsonOptions, ct);
        return await ParseAsync<GlobalVariableFolderResponse>(res, ct);
    }

    public async Task RenameGlobalFolderAsync(Guid id, UpdateGlobalVariableFolderRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PutAsJsonAsync($"api/global-variable-folders/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task MoveGlobalFolderAsync(Guid id, MoveGlobalVariableFolderRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsJsonAsync($"api/global-variable-folders/{id}/move", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteGlobalFolderAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.DeleteAsync($"api/global-variable-folders/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    // ---- Alerting Rules -----------------------------------------------------

    public async Task<List<NotificationRuleResponse>> ListAlertingRulesAsync(CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync("api/alerting/rules", ct);
        return await ParseAsync<List<NotificationRuleResponse>>(res, ct);
    }

    public async Task<NotificationRuleResponse> GetAlertingRuleAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync($"api/alerting/rules/{id}", ct);
        return await ParseAsync<NotificationRuleResponse>(res, ct);
    }

    public async Task<NotificationRuleResponse> CreateAlertingRuleAsync(SaveNotificationRuleRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsJsonAsync("api/alerting/rules", req, JsonOptions, ct);
        return await ParseAsync<NotificationRuleResponse>(res, ct);
    }

    public async Task UpdateAlertingRuleAsync(Guid id, SaveNotificationRuleRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PutAsJsonAsync($"api/alerting/rules/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteAlertingRuleAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.DeleteAsync($"api/alerting/rules/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<TestFireResponse> TestFireAlertingRuleAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsync($"api/alerting/rules/{id}/test-fire", content: null, ct);
        return await ParseAsync<TestFireResponse>(res, ct);
    }

    public async Task<List<NotificationDeliveryDto>> ListAlertingDeliveriesAsync(Guid? ruleId, string? status, int limit, CancellationToken ct)
    {
        EnsureReady();
        var q = new List<string>();
        if (ruleId is { } rid) q.Add($"ruleId={rid}");
        if (!string.IsNullOrWhiteSpace(status)) q.Add($"status={Uri.EscapeDataString(status)}");
        if (limit > 0) q.Add($"limit={limit}");
        var qs = q.Count > 0 ? "?" + string.Join("&", q) : "";
        using var res = await _http.GetAsync($"api/alerting/deliveries{qs}", ct);
        return await ParseAsync<List<NotificationDeliveryDto>>(res, ct);
    }

    // ---- System-Alert Policies (ADR 0008) -----------------------------------

    public async Task<SystemAlertCatalogResponse> GetSystemAlertCatalogAsync(CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync("api/alerting/system/catalog", ct);
        return await ParseAsync<SystemAlertCatalogResponse>(res, ct);
    }

    public async Task<List<SystemAlertPolicyResponse>> ListSystemAlertPoliciesAsync(CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync("api/alerting/system/policies", ct);
        return await ParseAsync<List<SystemAlertPolicyResponse>>(res, ct);
    }

    public async Task<SystemAlertPolicyResponse> GetSystemAlertPolicyAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync($"api/alerting/system/policies/{id}", ct);
        return await ParseAsync<SystemAlertPolicyResponse>(res, ct);
    }

    public async Task<SystemAlertPolicyResponse> CreateSystemAlertPolicyAsync(SaveSystemAlertPolicyRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsJsonAsync("api/alerting/system/policies", req, JsonOptions, ct);
        return await ParseAsync<SystemAlertPolicyResponse>(res, ct);
    }

    public async Task UpdateSystemAlertPolicyAsync(Guid id, SaveSystemAlertPolicyRequest req, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PutAsJsonAsync($"api/alerting/system/policies/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteSystemAlertPolicyAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.DeleteAsync($"api/alerting/system/policies/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task EnableSystemAlertPolicyAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsync($"api/alerting/system/policies/{id}/enable", content: null, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DisableSystemAlertPolicyAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsync($"api/alerting/system/policies/{id}/disable", content: null, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<TestFireResponse> TestFireSystemAlertPolicyAsync(Guid id, CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.PostAsync($"api/alerting/system/policies/{id}/test-fire", content: null, ct);
        return await ParseAsync<TestFireResponse>(res, ct);
    }

    // ---- Plumbing -----------------------------------------------------------

    // ---- DbAdmin (read-only SQL / text2sql) ---------------------------------

    /// <summary>Schema catalog for every EF-tracked table. Admin-only server-side; hidden secret
    /// columns (PasswordHash/EncryptedPassword/byte[]) are excluded by the API, GlobalVariable.Value
    /// arrives masked as "***".</summary>
    public async Task<List<DbAdminTableInfo>> ListDbTablesAsync(CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync("api/dbadmin/tables", ct);
        return await ParseAsync<List<DbAdminTableInfo>>(res, ct);
    }

    /// <summary>DB provider + read-query limits (maxRows, timeout) so an agent can respect them.</summary>
    public async Task<DbAdminInfoResponse> GetDbInfoAsync(CancellationToken ct)
    {
        EnsureReady();
        using var res = await _http.GetAsync("api/dbadmin/info", ct);
        return await ParseAsync<DbAdminInfoResponse>(res, ct);
    }

    /// <summary>Execute a single read-only SQL statement. <paramref name="mode"/> is forced to
    /// "read" by the caller; the server additionally enforces a SELECT/WITH/EXPLAIN/SHOW/VALUES/TABLE
    /// keyword whitelist, single-statement, rollback-guarantee, row-cap and timeout.</summary>
    public async Task<DbAdminQueryResponse> ExecuteDbReadQueryAsync(string sql, CancellationToken ct)
    {
        EnsureReady();
        var req = new DbAdminQueryRequest(sql, Mode: "read");
        using var res = await _http.PostAsJsonAsync("api/dbadmin/query", req, JsonOptions, ct);
        return await ParseAsync<DbAdminQueryResponse>(res, ct);
    }

    private static async Task<T> ParseAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        await EnsureSuccessAsync(res, ct);
        if (res.StatusCode == HttpStatusCode.NoContent || res.Content.Headers.ContentLength == 0)
            return default!;
        var stream = await res.Content.ReadAsStreamAsync(ct);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
        if (value is null) throw new ApiException(res.StatusCode, "EmptyBody", "Server returned empty body.", null);
        return value;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        string? title = null, detail = null;
        if (!string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("title", out var t)) title = t.GetString();
                if (doc.RootElement.TryGetProperty("detail", out var d)) detail = d.GetString();
                if (detail is null && doc.RootElement.TryGetProperty("error", out var e)) detail = e.GetString();
            }
            catch (JsonException) { /* leave body as raw */ }
        }
        throw new ApiException(res.StatusCode, title, detail, body);
    }
}

/// <summary>Thrown when the server URL is not configured — distinct from an HTTP error.</summary>
public sealed class NotConfiguredException : Exception
{
    public NotConfiguredException(string message) : base(message) { }
}
