using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using NodePilot.Mcp.Api;
using NodePilot.Mcp.Api.Dtos;
using NodePilot.Mcp.Mapping;

namespace NodePilot.Mcp.Tools;

/// <summary>
/// Destructive / bulk / admin tools. This class is registered ONLY when
/// NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true (see Program.cs) — so by default the agent never even
/// sees these in tools/list and cannot "try" them. Deliberately split out from the safe tools.
/// (Grows with delete_* and force_unlock_workflow in later phases.)
/// </summary>
[McpServerToolType]
public sealed class DestructiveTools
{
    private readonly NodePilotApiClient _api;

    public DestructiveTools(NodePilotApiClient api) => _api = api;

    [McpServerTool(Name = "cancel_all_executions", Destructive = true)]
    [Description("DESTRUCTIVE incident kill-switch: cancel ALL running executions of a workflow at once (Admin/Operator). Returns how many were signalled. Only available when NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true.")]
    public async Task<object> CancelAllExecutions(
        [Description("The workflow GUID whose running executions should all be cancelled.")] string workflowId,
        CancellationToken cancellationToken = default)
    {
        var id = ExecutionTools.ParseGuid(workflowId, "workflowId");
        var res = await ApiErrorMapper.Guard(() => _api.CancelAllAsync(id, cancellationToken));
        return new { workflowId = id, total = res.Total, signalled = res.Signalled };
    }

    [McpServerTool(Name = "test_step", Destructive = true)]
    [Description("DESTRUCTIVE: run one real activity in isolation with mock upstream variables. An optional unsaved config override additionally requires workflow Edit permission and your active edit lock. Only available when NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true.")]
    public async Task<object> TestStep(
        [Description("The workflow GUID or exact name.")] string idOrName,
        [Description("The step (node) id to test.")] string stepId,
        [Description("Optional mock upstream variables, flat map of 'stepName.field' → value (e.g. 'checkDisk.param.freeGb' → '7').")] Dictionary<string, string>? mockVariables = null,
        [Description("Optional unsaved data.config override. Requires Edit permission and your active workflow edit lock.")] JsonElement? configOverride = null,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        var req = new StepTestRequest(mockVariables, configOverride);
        var result = await ApiErrorMapper.Guard(() =>
            _api.TestStepAsync(wf.Id, stepId, req, cancellationToken));
        return new
        {
            success = result.Success,
            output = PayloadShaping.Truncate(result.Output),
            errorOutput = PayloadShaping.Truncate(result.ErrorOutput),
            outputParameters = result.OutputParameters,
            durationMs = result.DurationMs,
            errorMessage = result.ErrorMessage,
        };
    }

    [McpServerTool(Name = "delete_workflow", Destructive = true)]
    [Description("DESTRUCTIVE (Admin): permanently delete a workflow and cascade its executions, versions and stats. Irreversible. Only available when NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true.")]
    public async Task<object> DeleteWorkflow(
        [Description("The workflow GUID or exact name to delete.")] string idOrName,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        await ApiErrorMapper.Guard(() => _api.DeleteWorkflowAsync(wf.Id, cancellationToken));
        return new { deleted = true, workflowId = wf.Id, name = wf.Name };
    }

    [McpServerTool(Name = "force_unlock_workflow", Destructive = true)]
    [Description("DESTRUCTIVE (Admin): break another user's edit lock on a workflow. Audited. Only available when NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true.")]
    public async Task<object> ForceUnlockWorkflow(
        [Description("The workflow GUID or exact name.")] string idOrName,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        var r = await ApiErrorMapper.Guard(() => _api.ForceUnlockWorkflowAsync(wf.Id, cancellationToken));
        return new { forceUnlocked = true, workflowId = r.Id };
    }

    [McpServerTool(Name = "delete_machine", Destructive = true)]
    [Description("DESTRUCTIVE (Admin): permanently delete a managed machine. Only available when NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true.")]
    public async Task<object> DeleteMachine(
        [Description("The machine GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var guid = ExecutionTools.ParseGuid(id, "machine id");
        await ApiErrorMapper.Guard(() => _api.DeleteMachineAsync(guid, cancellationToken));
        return new { deleted = true, machineId = guid };
    }

    [McpServerTool(Name = "delete_credential", Destructive = true)]
    [Description("DESTRUCTIVE (Admin): permanently delete a credential. Only available when NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true.")]
    public async Task<object> DeleteCredential(
        [Description("The credential GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var guid = ExecutionTools.ParseGuid(id, "credential id");
        await ApiErrorMapper.Guard(() => _api.DeleteCredentialAsync(guid, cancellationToken));
        return new { deleted = true, credentialId = guid };
    }

    [McpServerTool(Name = "delete_global_variable", Destructive = true)]
    [Description("DESTRUCTIVE (Admin): permanently delete a global variable. Only available when NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true.")]
    public async Task<object> DeleteGlobalVariable(
        [Description("The global variable GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var guid = ExecutionTools.ParseGuid(id, "global id");
        await ApiErrorMapper.Guard(() => _api.DeleteGlobalAsync(guid, cancellationToken));
        return new { deleted = true, globalId = guid };
    }

    [McpServerTool(Name = "delete_global_variable_folder", Destructive = true)]
    [Description("DESTRUCTIVE (Admin): permanently delete an EMPTY global-variable folder (fails 409 if it still has sub-folders or variables). Only available when NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true.")]
    public async Task<object> DeleteGlobalVariableFolder(
        [Description("The folder GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var guid = ExecutionTools.ParseGuid(id, "folder id");
        await ApiErrorMapper.Guard(() => _api.DeleteGlobalFolderAsync(guid, cancellationToken));
        return new { deleted = true, folderId = guid };
    }

    [McpServerTool(Name = "delete_alerting_rule", Destructive = true)]
    [Description("DESTRUCTIVE (Admin): permanently delete an alerting rule. Only available when NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true.")]
    public async Task<object> DeleteAlertingRule(
        [Description("The rule GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var guid = ExecutionTools.ParseGuid(id, "rule id");
        await ApiErrorMapper.Guard(() => _api.DeleteAlertingRuleAsync(guid, cancellationToken));
        return new { deleted = true, ruleId = guid };
    }

    [McpServerTool(Name = "delete_system_alert_policy", Destructive = true)]
    [Description("DESTRUCTIVE (Admin): permanently delete a system-alert policy. Only available when NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true.")]
    public async Task<object> DeleteSystemAlertPolicy(
        [Description("The policy GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var guid = ExecutionTools.ParseGuid(id, "policy id");
        await ApiErrorMapper.Guard(() => _api.DeleteSystemAlertPolicyAsync(guid, cancellationToken));
        return new { deleted = true, policyId = guid };
    }

    private async Task<Api.Dtos.WorkflowResponse> ResolveAsync(string idOrName, CancellationToken ct)
        => Guid.TryParse(idOrName, out var id)
            ? await ApiErrorMapper.Guard(() => _api.GetWorkflowAsync(id, ct))
            : await ApiErrorMapper.Guard(() => _api.GetWorkflowByNameAsync(idOrName, ct));
}
