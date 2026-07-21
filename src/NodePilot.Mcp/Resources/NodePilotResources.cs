using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using NodePilot.Core.Activities;

namespace NodePilot.Mcp.Resources;

/// <summary>
/// MCP resources an agent can load once for context (instead of re-deriving via tool calls):
/// the activity catalog, the curated per-activity config reference, and the workflow layout
/// styleguide. The latter two are served from the EMBEDDED copies (no docs/ on disk after a
/// `dotnet tool install`). Registered explicitly in Program.cs via WithResources&lt;T&gt;().
/// </summary>
[McpServerResourceType]
public sealed class NodePilotResources
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [McpServerResource(UriTemplate = "nodepilot://activity-catalog", Name = "activity-catalog", MimeType = "application/json")]
    [Description("The full NodePilot activity/trigger catalog: type, category, isTrigger/isRemote flags and stable output parameters. Computed in-process.")]
    public static string ActivityCatalog()
    {
        var rows = NodePilot.Core.Activities.ActivityCatalog.All.Select(a => new
        {
            type = a.Type,
            category = a.Category.ToString(),
            isTrigger = a.IsTrigger,
            isRemote = a.IsRemote,
            outputParameters = a.OutputParameters.Select(p => new { name = p.Name, type = p.Type }),
        });
        return JsonSerializer.Serialize(new { activityTypes = rows }, Json);
    }

    [McpServerResource(UriTemplate = "nodepilot://activity-config-reference", Name = "activity-config-reference", MimeType = "application/json")]
    [Description("Curated per-activity config-key reference (key/type/required/description + example) — the CONFIG-key schema the backend catalog does not carry. Use it to author valid node config objects.")]
    public static string ActivityConfigReference()
        => EmbeddedResources.Read(EmbeddedResources.ActivityConfigReference);

    [McpServerResource(UriTemplate = "nodepilot://styleguide", Name = "workflow-styleguide", MimeType = "text/markdown")]
    [Description("The NodePilot workflow layout/naming styleguide (docs/workflow-styleguide.md), embedded into the tool. Read this before generating or restructuring a workflow graph.")]
    public static string Styleguide()
        => EmbeddedResources.Read(EmbeddedResources.WorkflowStyleguide);
}
