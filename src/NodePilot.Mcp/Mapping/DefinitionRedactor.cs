using System.Text.Json;
using System.Text.Json.Nodes;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Mcp.Mapping;

/// <summary>
/// Masks secret-bearing config values inside a workflow definition before it is handed to the
/// agent. The NodePilot API only redacts the definition for non-privileged (Viewer) roles —
/// an Admin/Operator service account (which the MCP server typically runs as) receives the RAW
/// DefinitionJson with inline webhook secrets / API keys / passwords. This re-applies the same
/// masking the API uses for Viewers, using the shared Core key set
/// (<see cref="WorkflowSecretKeys.SecretConfigKeys"/>), so secrets never reach the model.
/// </summary>
public static class DefinitionRedactor
{
    /// <summary>Return a redacted copy of the definition with secret config values masked to "***".
    /// Thin wrapper over the shared Core walk so the MCP redaction can never drift from the API/AI paths.</summary>
    public static JsonNode Redact(JsonElement root) => WorkflowSecretRedactor.Redact(root);
}
