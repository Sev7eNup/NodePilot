using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NodePilot.Cli.Api.Dtos;

namespace NodePilot.Cli.Api;

/// <summary>
/// Typed HttpClient wrapper around the NodePilot REST API. Authentication is via Bearer
/// header — accepted by the server alongside the SPA's httpOnly cookie. One method per
/// endpoint; non-2xx responses always become <see cref="ApiException"/> so commands
/// branch on a single exception type.
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

    // ---- Auth ---------------------------------------------------------------

    public async Task<LoginResponse> LoginAsync(LoginRequest req, string? setupToken, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
        {
            Content = JsonContent.Create(req, options: JsonOptions),
        };
        if (!string.IsNullOrWhiteSpace(setupToken))
            msg.Headers.Add("X-Setup-Token", setupToken);
        // Opt in to the JWT being returned in the response body. The browser SPA omits this
        // header and authenticates via the httpOnly np_auth cookie instead; the CLI is a
        // stateless Bearer client and must receive the token to store it. Safe on this
        // password-gated path — only a caller with valid credentials ever gets a 200 here.
        msg.Headers.Add("X-Auth-Token-Response", "true");

        using var res = await _http.SendAsync(msg, ct);
        return await ParseAsync<LoginResponse>(res, ct);
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        using var res = await _http.PostAsync("api/auth/logout", content: null, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<LoginResponse> RefreshAsync(CancellationToken ct)
    {
        using var res = await _http.PostAsync("api/auth/refresh", content: null, ct);
        return await ParseAsync<LoginResponse>(res, ct);
    }

    public async Task<MeResponse> MeAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/auth/me", ct);
        return await ParseAsync<MeResponse>(res, ct);
    }

    // ---- Workflows ----------------------------------------------------------

    public async Task<List<WorkflowResponse>> ListWorkflowsAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/workflows", ct);
        return await ParseAsync<List<WorkflowResponse>>(res, ct);
    }

    public async Task<WorkflowResponse> GetWorkflowAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/workflows/{id}", ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task<ExecutionResponse> ExecuteWorkflowAsync(Guid id, ExecuteWorkflowRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync($"api/workflows/{id}/execute", req, JsonOptions, ct);
        return await ParseAsync<ExecutionResponse>(res, ct);
    }

    public async Task<WorkflowResponse> LockWorkflowAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"api/workflows/{id}/lock", content: null, ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task<WorkflowResponse> UnlockWorkflowAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"api/workflows/{id}/unlock", content: null, ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task<WorkflowResponse> PublishWorkflowAsync(Guid id, PublishWorkflowRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync($"api/workflows/{id}/publish", req, JsonOptions, ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task EnableWorkflowAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"api/workflows/{id}/enable", content: null, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DisableWorkflowAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"api/workflows/{id}/disable", content: null, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<CancelAllResponse> CancelAllAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"api/workflows/{id}/cancel-all", content: null, ct);
        return await ParseAsync<CancelAllResponse>(res, ct);
    }

    public async Task<WorkflowResponse> DuplicateWorkflowAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"api/workflows/{id}/duplicate", content: null, ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task DeleteWorkflowAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.DeleteAsync($"api/workflows/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<WorkflowExportEnvelope> ExportOneAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/workflows/{id}/export", ct);
        return await ParseAsync<WorkflowExportEnvelope>(res, ct);
    }

    public async Task<WorkflowExportEnvelope> ExportAllAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/workflows/export", ct);
        return await ParseAsync<WorkflowExportEnvelope>(res, ct);
    }

    public async Task<ImportWorkflowsResponse> ImportAsync(WorkflowExportEnvelope envelope, Guid? folderId, CancellationToken ct)
    {
        var path = folderId is null ? "api/workflows/import" : $"api/workflows/import?folderId={folderId}";
        using var res = await _http.PostAsJsonAsync(path, envelope, JsonOptions, ct);
        return await ParseAsync<ImportWorkflowsResponse>(res, ct);
    }

    // ---- System-configuration backup (ADR 0001 — full disaster-recovery snapshot/restore) ----

    public async Task<BackupManifestResponse> GetBackupManifestAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/backup/manifest", ct);
        return await ParseAsync<BackupManifestResponse>(res, ct);
    }

    /// <summary>Posts an export request and returns the raw <c>.npbackup</c> bytes + warning count.</summary>
    public async Task<(byte[] Content, int Warnings)> ExportBackupAsync(
        List<string> sections, string passphrase, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync(
            "api/backup/export", new BackupExportRequest(sections, passphrase), JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        var warnings = 0;
        if (res.Headers.TryGetValues("X-Backup-Warnings", out var vals)
            && int.TryParse(vals.FirstOrDefault(), out var w)) warnings = w;
        return (bytes, warnings);
    }

    public async Task<BackupPreviewResult> PreviewBackupAsync(byte[] content, string? passphrase, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", "backup.npbackup" } };
        if (!string.IsNullOrEmpty(passphrase)) form.Add(new StringContent(passphrase), "passphrase");
        using var res = await _http.PostAsync("api/backup/preview", form, ct);
        return await ParseAsync<BackupPreviewResult>(res, ct);
    }

    public async Task<BackupRestoreResult> RestoreBackupAsync(byte[] content, string passphrase, string? policy, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(content), "file", "backup.npbackup" },
            { new StringContent(passphrase), "passphrase" },
        };
        if (!string.IsNullOrEmpty(policy)) form.Add(new StringContent(policy), "policy");
        using var res = await _http.PostAsync("api/backup/restore", form, ct);
        return await ParseAsync<BackupRestoreResult>(res, ct);
    }

    public async Task<List<WorkflowVersionInfo>> ListVersionsAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/workflows/{id}/versions", ct);
        return await ParseAsync<List<WorkflowVersionInfo>>(res, ct);
    }

    public async Task<WorkflowResponse> RollbackAsync(Guid id, int version, RollbackRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync($"api/workflows/{id}/rollback/{version}", req, JsonOptions, ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task<WorkflowVersionDetail> GetVersionAsync(Guid id, int version, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/workflows/{id}/versions/{version}", ct);
        return await ParseAsync<WorkflowVersionDetail>(res, ct);
    }

    public async Task<WorkflowResponse> ForceUnlockWorkflowAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"api/workflows/{id}/force-unlock", content: null, ct);
        return await ParseAsync<WorkflowResponse>(res, ct);
    }

    public async Task<ScorchImportResponse> ImportScorchAsync(string xml, Guid? folderId, CancellationToken ct)
    {
        var path = folderId is null ? "api/workflows/import-scorch" : $"api/workflows/import-scorch?folderId={folderId}";
        using var content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml");
        using var res = await _http.PostAsync(path, content, ct);
        return await ParseAsync<ScorchImportResponse>(res, ct);
    }

    public async Task<Dictionary<string, StepStats>> GetStepStatsAsync(Guid workflowId, int? windowDays, CancellationToken ct)
    {
        var path = windowDays.HasValue
            ? $"api/workflows/{workflowId}/step-stats?windowDays={windowDays.Value}"
            : $"api/workflows/{workflowId}/step-stats";
        using var res = await _http.GetAsync(path, ct);
        return await ParseAsync<Dictionary<string, StepStats>>(res, ct);
    }

    public async Task<Dictionary<string, List<StepHealthEntry>>> GetStepHealthAsync(
        Guid workflowId, int? limit, CancellationToken ct)
    {
        var path = limit.HasValue
            ? $"api/workflows/{workflowId}/step-health?limit={limit.Value}"
            : $"api/workflows/{workflowId}/step-health";
        using var res = await _http.GetAsync(path, ct);
        return await ParseAsync<Dictionary<string, List<StepHealthEntry>>>(res, ct);
    }

    // ---- Executions ---------------------------------------------------------

    public async Task<List<ExecutionResponse>> ListExecutionsAsync(Guid? workflowId, CancellationToken ct)
    {
        var path = workflowId.HasValue ? $"api/executions?workflowId={workflowId}" : "api/executions";
        using var res = await _http.GetAsync(path, ct);
        return await ParseAsync<List<ExecutionResponse>>(res, ct);
    }

    public async Task<ExecutionResponse> GetExecutionAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/executions/{id}", ct);
        return await ParseAsync<ExecutionResponse>(res, ct);
    }

    public async Task<List<StepExecutionResponse>> GetStepsAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/executions/{id}/steps", ct);
        return await ParseAsync<List<StepExecutionResponse>>(res, ct);
    }

    public async Task CancelExecutionAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"api/executions/{id}/cancel", content: null, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<ExecutionResponse> RetryExecutionAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"api/executions/{id}/retry", content: null, ct);
        return await ParseAsync<ExecutionResponse>(res, ct);
    }

    public async Task ResumeExecutionAsync(Guid id, ResumeDebugRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync($"api/executions/{id}/resume", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<List<string>> GetPausedStepsAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/executions/{id}/paused-steps", ct);
        return await ParseAsync<List<string>>(res, ct);
    }

    // ---- Machines -----------------------------------------------------------

    public async Task<List<MachineResponse>> ListMachinesAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/machines", ct);
        return await ParseAsync<List<MachineResponse>>(res, ct);
    }

    public async Task<MachineResponse> GetMachineAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/machines/{id}", ct);
        return await ParseAsync<MachineResponse>(res, ct);
    }

    public async Task<MachineResponse> CreateMachineAsync(CreateMachineRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("api/machines", req, JsonOptions, ct);
        return await ParseAsync<MachineResponse>(res, ct);
    }

    public async Task UpdateMachineAsync(Guid id, UpdateMachineRequest req, CancellationToken ct)
    {
        using var res = await _http.PutAsJsonAsync($"api/machines/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteMachineAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.DeleteAsync($"api/machines/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<TestConnectionResponse> TestMachineAsync(Guid id, TestConnectionRequest? req, CancellationToken ct)
    {
        // Body is optional on the server — when null we still send an empty JSON body
        // because the server's [FromBody] attribute permits null but model binding can
        // reject empty content depending on the negotiated content-type.
        using var res = await _http.PostAsJsonAsync($"api/machines/{id}/test", req ?? new TestConnectionRequest(null), JsonOptions, ct);
        return await ParseAsync<TestConnectionResponse>(res, ct);
    }

    // ---- Credentials --------------------------------------------------------

    public async Task<List<CredentialResponse>> ListCredentialsAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/credentials", ct);
        return await ParseAsync<List<CredentialResponse>>(res, ct);
    }

    public async Task<CredentialResponse> GetCredentialAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/credentials/{id}", ct);
        return await ParseAsync<CredentialResponse>(res, ct);
    }

    public async Task<CredentialResponse> CreateCredentialAsync(CreateCredentialRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("api/credentials", req, JsonOptions, ct);
        return await ParseAsync<CredentialResponse>(res, ct);
    }

    public async Task UpdateCredentialAsync(Guid id, UpdateCredentialRequest req, CancellationToken ct)
    {
        using var res = await _http.PutAsJsonAsync($"api/credentials/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteCredentialAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.DeleteAsync($"api/credentials/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    // ---- Global Variables ---------------------------------------------------

    public async Task<List<GlobalVariableResponse>> ListGlobalVariablesAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/global-variables", ct);
        return await ParseAsync<List<GlobalVariableResponse>>(res, ct);
    }

    public async Task<GlobalVariableResponse> CreateGlobalVariableAsync(CreateGlobalVariableRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("api/global-variables", req, JsonOptions, ct);
        return await ParseAsync<GlobalVariableResponse>(res, ct);
    }

    public async Task UpdateGlobalVariableAsync(Guid id, UpdateGlobalVariableRequest req, CancellationToken ct)
    {
        using var res = await _http.PutAsJsonAsync($"api/global-variables/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteGlobalVariableAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.DeleteAsync($"api/global-variables/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task MoveGlobalVariableToFolderAsync(Guid id, Guid folderId, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync($"api/global-variables/{id}/move-folder", new MoveGlobalVariableRequest(folderId), JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    // ---- Global Variable Folders --------------------------------------------

    public async Task<List<GlobalVariableFolderResponse>> ListGlobalVariableFoldersAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/global-variable-folders", ct);
        return await ParseAsync<List<GlobalVariableFolderResponse>>(res, ct);
    }

    public async Task<GlobalVariableFolderResponse> CreateGlobalVariableFolderAsync(CreateGlobalVariableFolderRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("api/global-variable-folders", req, JsonOptions, ct);
        return await ParseAsync<GlobalVariableFolderResponse>(res, ct);
    }

    public async Task RenameGlobalVariableFolderAsync(Guid id, UpdateGlobalVariableFolderRequest req, CancellationToken ct)
    {
        using var res = await _http.PutAsJsonAsync($"api/global-variable-folders/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task MoveGlobalVariableFolderAsync(Guid id, MoveGlobalVariableFolderRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync($"api/global-variable-folders/{id}/move", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteGlobalVariableFolderAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.DeleteAsync($"api/global-variable-folders/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    // ---- Maintenance Windows ------------------------------------------------

    public async Task<List<MaintenanceWindowResponse>> ListMaintenanceWindowsAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/maintenance-windows", ct);
        return await ParseAsync<List<MaintenanceWindowResponse>>(res, ct);
    }

    public async Task<MaintenanceWindowResponse> CreateMaintenanceWindowAsync(CreateMaintenanceWindowRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("api/maintenance-windows", req, JsonOptions, ct);
        return await ParseAsync<MaintenanceWindowResponse>(res, ct);
    }

    public async Task UpdateMaintenanceWindowAsync(Guid id, UpdateMaintenanceWindowRequest req, CancellationToken ct)
    {
        using var res = await _http.PutAsJsonAsync($"api/maintenance-windows/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteMaintenanceWindowAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.DeleteAsync($"api/maintenance-windows/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    // ---- Alerting Rules -----------------------------------------------------

    public async Task<List<NotificationRuleResponse>> ListAlertingRulesAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/alerting/rules", ct);
        return await ParseAsync<List<NotificationRuleResponse>>(res, ct);
    }

    public async Task<NotificationRuleResponse> GetAlertingRuleAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/alerting/rules/{id}", ct);
        return await ParseAsync<NotificationRuleResponse>(res, ct);
    }

    public async Task<NotificationRuleResponse> CreateAlertingRuleAsync(SaveNotificationRuleRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("api/alerting/rules", req, JsonOptions, ct);
        return await ParseAsync<NotificationRuleResponse>(res, ct);
    }

    public async Task UpdateAlertingRuleAsync(Guid id, SaveNotificationRuleRequest req, CancellationToken ct)
    {
        using var res = await _http.PutAsJsonAsync($"api/alerting/rules/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteAlertingRuleAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.DeleteAsync($"api/alerting/rules/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<TestFireResponse> TestFireAlertingRuleAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"api/alerting/rules/{id}/test-fire", content: null, ct);
        return await ParseAsync<TestFireResponse>(res, ct);
    }

    public async Task<List<NotificationDeliveryDto>> ListAlertingDeliveriesAsync(Guid? ruleId, string? status, int limit, CancellationToken ct)
    {
        var q = new List<string>();
        if (ruleId is { } rid) q.Add($"ruleId={rid}");
        if (!string.IsNullOrWhiteSpace(status)) q.Add($"status={Uri.EscapeDataString(status)}");
        if (limit > 0) q.Add($"limit={limit}");
        var qs = q.Count > 0 ? "?" + string.Join("&", q) : "";
        using var res = await _http.GetAsync($"api/alerting/deliveries{qs}", ct);
        return await ParseAsync<List<NotificationDeliveryDto>>(res, ct);
    }

    // ---- System-Alert Policies (ADR 0008 — configurable policies that replaced the old built-in gauge alerts) ----

    public async Task<SystemAlertCatalogResponse> GetSystemAlertCatalogAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/alerting/system/catalog", ct);
        return await ParseAsync<SystemAlertCatalogResponse>(res, ct);
    }

    public async Task<List<SystemAlertPolicyResponse>> ListSystemAlertPoliciesAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/alerting/system/policies", ct);
        return await ParseAsync<List<SystemAlertPolicyResponse>>(res, ct);
    }

    public async Task<SystemAlertPolicyResponse> GetSystemAlertPolicyAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/alerting/system/policies/{id}", ct);
        return await ParseAsync<SystemAlertPolicyResponse>(res, ct);
    }

    public async Task<SystemAlertPolicyResponse> CreateSystemAlertPolicyAsync(SaveSystemAlertPolicyRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("api/alerting/system/policies", req, JsonOptions, ct);
        return await ParseAsync<SystemAlertPolicyResponse>(res, ct);
    }

    public async Task UpdateSystemAlertPolicyAsync(Guid id, SaveSystemAlertPolicyRequest req, CancellationToken ct)
    {
        using var res = await _http.PutAsJsonAsync($"api/alerting/system/policies/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteSystemAlertPolicyAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.DeleteAsync($"api/alerting/system/policies/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task EnableSystemAlertPolicyAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"api/alerting/system/policies/{id}/enable", content: null, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DisableSystemAlertPolicyAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"api/alerting/system/policies/{id}/disable", content: null, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<TestFireResponse> TestFireSystemAlertPolicyAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"api/alerting/system/policies/{id}/test-fire", content: null, ct);
        return await ParseAsync<TestFireResponse>(res, ct);
    }

    // ---- Users (Admin) ------------------------------------------------------

    public async Task<List<UserResponse>> ListUsersAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/users", ct);
        return await ParseAsync<List<UserResponse>>(res, ct);
    }

    public async Task<UserResponse> CreateUserAsync(CreateUserRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("api/users", req, JsonOptions, ct);
        return await ParseAsync<UserResponse>(res, ct);
    }

    public async Task UpdateUserAsync(Guid id, UpdateUserRequest req, CancellationToken ct)
    {
        using var res = await _http.PutAsJsonAsync($"api/users/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.DeleteAsync($"api/users/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    // ---- Dashboard / Observability -----------------------------------------

    public async Task<DashboardStats> GetDashboardAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/stats/dashboard", ct);
        return await ParseAsync<DashboardStats>(res, ct);
    }

    public async Task<TelemetrySummaryResponse> GetObservabilitySummaryAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/observability/summary", ct);
        return await ParseAsync<TelemetrySummaryResponse>(res, ct);
    }

    public async Task<OperationsGraphResponse> GetOperationsGraphAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/operations/graph", ct);
        return await ParseAsync<OperationsGraphResponse>(res, ct);
    }

    // ---- Audit / Triggers / Health -----------------------------------------

    public async Task<AuditPageResponse> AuditAsync(
        string? action, string? resourceType, Guid? resourceId, Guid? userId,
        string? ipAddress, DateTime? since, DateTime? until,
        DateTime? afterTs, Guid? afterId, int? take, CancellationToken ct)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(action))       query.Add($"action={Uri.EscapeDataString(action)}");
        if (!string.IsNullOrWhiteSpace(resourceType)) query.Add($"resourceType={Uri.EscapeDataString(resourceType)}");
        if (resourceId.HasValue) query.Add($"resourceId={resourceId}");
        if (userId.HasValue)     query.Add($"userId={userId}");
        if (!string.IsNullOrWhiteSpace(ipAddress)) query.Add($"ipAddress={Uri.EscapeDataString(ipAddress)}");
        if (since.HasValue)      query.Add($"since={Uri.EscapeDataString(since.Value.ToString("o"))}");
        if (until.HasValue)      query.Add($"until={Uri.EscapeDataString(until.Value.ToString("o"))}");
        if (afterTs.HasValue)    query.Add($"afterTs={Uri.EscapeDataString(afterTs.Value.ToString("o"))}");
        if (afterId.HasValue)    query.Add($"afterId={afterId}");
        if (take.HasValue)       query.Add($"take={take}");
        var path = "api/audit" + (query.Count == 0 ? "" : "?" + string.Join("&", query));

        using var res = await _http.GetAsync(path, ct);
        return await ParseAsync<AuditPageResponse>(res, ct);
    }

    public async Task<NextFiresResponse> CronNextFiresAsync(string cron, int count, CancellationToken ct)
    {
        var path = $"api/triggers/schedule/next-fires?cron={Uri.EscapeDataString(cron)}&count={count}";
        using var res = await _http.GetAsync(path, ct);
        return await ParseAsync<NextFiresResponse>(res, ct);
    }

    public async Task<(bool Live, bool Ready, string? ReadyDetail, string? LeaderStatus)> HealthAsync(CancellationToken ct)
    {
        bool live = false, ready = false;
        string? readyDetail = null, leaderStatus = null;
        try
        {
            using var liveRes = await _http.GetAsync("healthz/live", ct);
            live = liveRes.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { live = false; }

        try
        {
            using var readyRes = await _http.GetAsync("healthz/ready", ct);
            ready = readyRes.IsSuccessStatusCode;
            if (!ready) readyDetail = await readyRes.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex) { ready = false; readyDetail = ex.Message; }

        // /healthz/leader is fail-closed by design: a passive HA follower answers 503
        // ("follower"). That is a healthy state for the node, so the status string is
        // surfaced for display only and never folded into Live/Ready.
        try
        {
            using var leaderRes = await _http.GetAsync("healthz/leader", ct);
            var body = await leaderRes.Content.ReadAsStringAsync(ct);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                leaderStatus = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            }
            catch (System.Text.Json.JsonException) { leaderStatus = null; }
        }
        catch (HttpRequestException) { leaderStatus = null; }

        return (live, ready, readyDetail, leaderStatus);
    }

    // ---- Auth: methods discovery -------------------------------------------

    public async Task<AuthMethodsResponse> GetAuthMethodsAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/auth/methods", ct);
        return await ParseAsync<AuthMethodsResponse>(res, ct);
    }

    // ---- Workflow Contract / Coverage --------------------------------------

    public async Task<WorkflowContractResponse> GetContractAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/workflows/{id}/contract", ct);
        return await ParseAsync<WorkflowContractResponse>(res, ct);
    }

    public async Task<WorkflowContractResponse> GetContractByNameAsync(string name, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/workflows/by-name/{Uri.EscapeDataString(name)}/contract", ct);
        return await ParseAsync<WorkflowContractResponse>(res, ct);
    }

    public async Task<WorkflowCoverageResponse> GetCoverageAsync(Guid id, int? windowDays, CancellationToken ct)
    {
        var path = windowDays.HasValue
            ? $"api/workflows/{id}/coverage?windowDays={windowDays.Value}"
            : $"api/workflows/{id}/coverage";
        using var res = await _http.GetAsync(path, ct);
        return await ParseAsync<WorkflowCoverageResponse>(res, ct);
    }

    // ---- External Trigger (anonymous, X-Api-Key) ----------------------------

    /// <summary>
    /// Fires <c>POST /api/trigger/{name}</c> with the supplied API key + optional Idempotency-Key
    /// header. Uses an anonymous HttpClient — no JWT bearer — because the endpoint is gated
    /// solely by X-Api-Key. Returns the execution payload plus the <c>Idempotent-Replayed</c>
    /// header indicator so callers can distinguish a cached replay from a fresh fire.
    /// </summary>
    public async Task<(ExecutionResponse Execution, bool IdempotentReplayed)> TriggerExternalAsync(
        string workflowNameOrId,
        string apiKey,
        Dictionary<string, string>? parameters,
        int? timeoutSeconds,
        string? idempotencyKey,
        CancellationToken ct)
    {
        var body = new ExecuteWorkflowRequest(parameters, timeoutSeconds, false);
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"api/trigger/{Uri.EscapeDataString(workflowNameOrId)}")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        msg.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            msg.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var res = await _http.SendAsync(msg, ct);
        var execution = await ParseAsync<ExecutionResponse>(res, ct);
        var replayed = res.Headers.TryGetValues("Idempotent-Replayed", out var values)
            && values.Any(v => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
        return (execution, replayed);
    }

    // ---- Admin Settings -----------------------------------------------------

    public async Task<SettingsStatusResponse> GetSettingsStatusAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/admin/settings/status", ct);
        return await ParseAsync<SettingsStatusResponse>(res, ct);
    }

    public async Task<SystemInfoResponse> GetSystemInfoAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/admin/settings/system-info", ct);
        return await ParseAsync<SystemInfoResponse>(res, ct);
    }

    /// <summary>
    /// Returns the snapshot of every section as a raw JsonDocument. The CLI does not
    /// parse the per-section DTOs — it forwards them as JSON to the operator — so we
    /// keep the response untyped here.
    /// </summary>
    public async Task<JsonDocument> GetSettingsSnapshotAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/admin/settings", ct);
        await EnsureSuccessAsync(res, ct);
        var stream = await res.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    /// <summary>
    /// Returns one section + its ETag. The ETag is returned both in the response body
    /// and as the <c>ETag</c> header; we return the header form so the CLI can replay
    /// it byte-for-byte on the next PUT without re-serializing the response.
    /// </summary>
    public async Task<(JsonDocument Body, string? Etag)> GetSettingsSectionAsync(string section, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/admin/settings/{Uri.EscapeDataString(section)}", ct);
        await EnsureSuccessAsync(res, ct);
        var etag = res.Headers.ETag?.Tag;
        var stream = await res.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return (doc, etag);
    }

    /// <summary>
    /// Writes <paramref name="payload"/> to the given section using <paramref name="ifMatch"/>
    /// as the ETag for optimistic concurrency. Returns the freshly-persisted snapshot. On
    /// 412 PreconditionFailed the server includes the current snapshot — surfaced through
    /// <see cref="ApiException"/> so callers can inspect <c>Body</c> if they want to render
    /// a diff.
    /// </summary>
    public async Task<JsonDocument> PutSettingsSectionAsync(
        string section, string ifMatch, JsonElement payload, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Put, $"api/admin/settings/{Uri.EscapeDataString(section)}")
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };
        msg.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var res = await _http.SendAsync(msg, ct);
        await EnsureSuccessAsync(res, ct);
        var stream = await res.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    public async Task<SettingsTestProbeResult> TestSmtpAsync(SmtpTestProbeRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("api/admin/settings/test/smtp", req, JsonOptions, ct);
        return await ParseAsync<SettingsTestProbeResult>(res, ct);
    }

    public async Task<SettingsTestProbeResult> TestLlmAsync(LlmTestProbeRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("api/admin/settings/test/llm", req, JsonOptions, ct);
        return await ParseAsync<SettingsTestProbeResult>(res, ct);
    }

    // ---- Secrets ------------------------------------------------------------

    /// <summary>
    /// Triggers the bulk re-encrypt sweep. The endpoint returns 207 Multi-Status when
    /// some rows could not be migrated — that is NOT a transport-level failure, so we
    /// accept it as success and let the caller branch on <see cref="ReencryptResult.PartialSuccess"/>.
    /// </summary>
    public async Task<ReencryptResult> ReencryptSecretsAsync(CancellationToken ct)
    {
        using var res = await _http.PostAsync("api/secrets/reencrypt", content: null, ct);
        // 207 Multi-Status carries a structured body; treat it like 200 for parsing.
        if ((int)res.StatusCode == 207)
        {
            var stream = await res.Content.ReadAsStreamAsync(ct);
            var partial = await JsonSerializer.DeserializeAsync<ReencryptResult>(stream, JsonOptions, ct)
                ?? throw new ApiException(res.StatusCode, "EmptyBody", "Server returned empty body.", null);
            return partial;
        }
        return await ParseAsync<ReencryptResult>(res, ct);
    }

    // ---- Shared workflow folders (RBAC) -------------------------------------

    public async Task<List<SharedFolderResponse>> ListSharedFoldersAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/shared-workflow-folders", ct);
        return await ParseAsync<List<SharedFolderResponse>>(res, ct);
    }

    public async Task<SharedFolderResponse> CreateSharedFolderAsync(CreateSharedFolderRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("api/shared-workflow-folders", req, JsonOptions, ct);
        return await ParseAsync<SharedFolderResponse>(res, ct);
    }

    public async Task RenameSharedFolderAsync(Guid id, UpdateSharedFolderRequest req, CancellationToken ct)
    {
        using var res = await _http.PutAsJsonAsync($"api/shared-workflow-folders/{id}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task MoveSharedFolderAsync(Guid id, MoveSharedFolderRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync($"api/shared-workflow-folders/{id}/move", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task DeleteSharedFolderAsync(Guid id, CancellationToken ct)
    {
        using var res = await _http.DeleteAsync($"api/shared-workflow-folders/{id}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task MoveWorkflowToFolderAsync(Guid workflowId, MoveWorkflowToFolderRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync($"api/workflows/{workflowId}/move-folder", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<List<SharedFolderPermissionResponse>> ListSharedFolderPermissionsAsync(Guid folderId, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"api/shared-workflow-folders/{folderId}/permissions", ct);
        return await ParseAsync<List<SharedFolderPermissionResponse>>(res, ct);
    }

    public async Task<SharedFolderPermissionResponse> GrantSharedFolderPermissionAsync(
        Guid folderId, GrantSharedFolderPermissionRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync($"api/shared-workflow-folders/{folderId}/permissions", req, JsonOptions, ct);
        return await ParseAsync<SharedFolderPermissionResponse>(res, ct);
    }

    public async Task UpdateSharedFolderPermissionAsync(
        Guid folderId, Guid permissionId, UpdateSharedFolderPermissionRequest req, CancellationToken ct)
    {
        using var res = await _http.PutAsJsonAsync($"api/shared-workflow-folders/{folderId}/permissions/{permissionId}", req, JsonOptions, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task RevokeSharedFolderPermissionAsync(Guid folderId, Guid permissionId, CancellationToken ct)
    {
        using var res = await _http.DeleteAsync($"api/shared-workflow-folders/{folderId}/permissions/{permissionId}", ct);
        await EnsureSuccessAsync(res, ct);
    }

    // ---- Observability raw query -------------------------------------------

    /// <summary>
    /// Forwards <c>GET /api/observability/query</c>. The server returns a raw Prometheus
    /// JSON payload (whatever the upstream returned, with the upstream status code preserved
    /// via ContentResult). Returned as a parsed JsonDocument so the CLI can pretty-print it.
    /// </summary>
    public async Task<JsonDocument> ObservabilityQueryAsync(string query, long? time, CancellationToken ct)
    {
        var path = $"api/observability/query?query={Uri.EscapeDataString(query)}"
                 + (time.HasValue ? $"&time={time.Value}" : "");
        using var res = await _http.GetAsync(path, ct);
        await EnsureSuccessAsync(res, ct);
        var stream = await res.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    public async Task<JsonDocument> ObservabilityQueryRangeAsync(
        string query, long start, long end, string step, CancellationToken ct)
    {
        var path = $"api/observability/query_range?query={Uri.EscapeDataString(query)}"
                 + $"&start={start}&end={end}&step={Uri.EscapeDataString(step)}";
        using var res = await _http.GetAsync(path, ct);
        await EnsureSuccessAsync(res, ct);
        var stream = await res.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    // ---- Step Test ----------------------------------------------------------

    public async Task<StepTestResponse> TestStepAsync(Guid workflowId, string stepId, StepTestRequest req, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync(
            $"api/workflows/{workflowId}/steps/{Uri.EscapeDataString(stepId)}/test",
            req, JsonOptions, ct);
        return await ParseAsync<StepTestResponse>(res, ct);
    }

    public async Task<StepTestContextResponse> GetStepTestContextAsync(
        Guid workflowId, string stepId, Guid? executionId, CancellationToken ct)
    {
        var path = $"api/workflows/{workflowId}/steps/{Uri.EscapeDataString(stepId)}/test-context"
                 + (executionId.HasValue ? $"?executionId={executionId.Value}" : "");
        using var res = await _http.GetAsync(path, ct);
        return await ParseAsync<StepTestContextResponse>(res, ct);
    }

    public async Task<List<StepTestContextRunInfo>> ListStepTestContextRunsAsync(
        Guid workflowId, string stepId, int? limit, CancellationToken ct)
    {
        var path = $"api/workflows/{workflowId}/steps/{Uri.EscapeDataString(stepId)}/test-context/runs"
                 + (limit.HasValue ? $"?limit={limit.Value}" : "");
        using var res = await _http.GetAsync(path, ct);
        return await ParseAsync<List<StepTestContextRunInfo>>(res, ct);
    }

    // ---- DbAdmin SQL Console (Admin) ---------------------------------------

    public async Task<DbAdminInfo> GetDbAdminInfoAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("api/dbadmin/info", ct);
        return await ParseAsync<DbAdminInfo>(res, ct);
    }

    /// <summary>
    /// Sends an ad-hoc SQL statement to the admin query console. Read-mode is the default;
    /// pass <paramref name="writeMode"/> = <c>true</c> to attempt a mutation (requires the
    /// server to have <c>DbAdmin:AllowWriteQueries=true</c>).
    /// </summary>
    public async Task<DbAdminQueryResponseDto> ExecuteDbAdminQueryAsync(string sql, bool writeMode, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "api/dbadmin/query")
        {
            Content = JsonContent.Create(new DbAdminQueryRequestDto(sql, writeMode ? "write" : "read"), options: JsonOptions),
        };
        if (writeMode) req.Headers.Add("X-Confirm-Write", "ALLOW");
        using var res = await _http.SendAsync(req, ct);
        return await ParseAsync<DbAdminQueryResponseDto>(res, ct);
    }

    // ---- Plumbing -----------------------------------------------------------

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
