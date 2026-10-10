using System.Text.Json;
using NodePilot.Core.Models;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Activities;

namespace NodePilot.Engine.Agents;

// Only synchronous child executions inherit this host-owned scope; it is never an input parameter.
internal sealed class AgentReadOnlyWorkflowScope : IDisposable
{
    private static readonly AsyncLocal<int> Depth = new();
    internal static bool IsActive => Depth.Value > 0;
    private readonly int _previous = Depth.Value;
    private AgentReadOnlyWorkflowScope() => Depth.Value++;
    internal static AgentReadOnlyWorkflowScope Enter() => new();
    public void Dispose() => Depth.Value = _previous;

    internal static void ValidateWorkflow(Workflow workflow)
    {
        if (!workflow.IsEnabled || workflow.CheckedOutByUserId is not null || workflow.PublishedByUserId is null)
            throw Denied("Only published, enabled, unlocked workflows may be called.");
        foreach (var node in WorkflowDefinitionDocument.Parse(workflow.DefinitionJson).Nodes.Where(n => !n.Data.Disabled))
        {
            if (node.Data.TargetMachineRaw?.Contains("{{") == true || node.Data.CredentialRaw?.Contains("{{") == true)
                throw Denied("Child targets and credentials must be fixed in the published workflow.");
            ValidateStep(node.Type, node.Data.Config);
        }
    }

    internal static void ValidateStep(string type, JsonElement config)
    {
        switch (type)
        {
            case "manualTrigger": case "log": case "returnData": case "generateText": case "jsonQuery":
            case "xmlQuery": case "decision": case "junction": case "delay": case "fileHash":
            case "aiAgent": case "aiAgentTeam": return;
            case "wmiQuery":
                var mode = (config.GetStringOrNull("mode") ?? "query").Trim().ToLowerInvariant();
                if (mode is not ("query" or "wql")) throw Denied("CIM methods are not read operations.");
                AgentPermissionPolicy.ValidateCimRead(mode == "query" ? config.GetStringOrNull("className") : null,
                    config.GetStringOrNull("namespace"), mode == "wql" ? config.GetStringOrNull("query") : null);
                return;
            case "restApi":
                var method = (config.GetStringOrNull("method") ?? "GET").ToUpperInvariant();
                if (method is not ("GET" or "HEAD") || config.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Null && body.ToString().Length != 0)
                    throw Denied("HTTP reads require GET/HEAD without a body.");
                return;
            case "runScript":
                if (config.GetBool("transcript", false)) throw Denied("Transcripts are not supported in read-only child scripts.");
                var script = config.GetStringOrNull("script") ?? "";
                // The executor checks the final script after its own safe template substitution.
                if (!script.Contains("{{")) AgentPermissionPolicy.PrepareShell("powershell", script);
                return;
            case "fileOperation" when config.GetStringOrNull("operation") == "exists": return;
            case "folderOperation" when config.GetStringOrNull("operation") is "exists" or "list": return;
            case "registryOperation" when (config.GetStringOrNull("operation") ?? "read").Trim().ToLowerInvariant() is "read" or "exists" or "listsubkeys" or "listvalues": return;
            case "startWorkflow":
                if (!config.GetBool("waitForCompletion", true) || !Guid.TryParse(config.GetStringOrNull("workflowNameOrId"), out _))
                    throw Denied("Nested workflows require a fixed ID and synchronous execution.");
                return;
            default: throw Denied($"Activity '{type}' has no supported read-only execution contract.");
        }
    }

    private static UnauthorizedAccessException Denied(string text) => new("Agent read-only workflow: " + text);
}
