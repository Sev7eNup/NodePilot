using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using NodePilot.Ai.Agents;
using NodePilot.Core.Agents;

namespace NodePilot.Engine.Agents;

public sealed class AgentExternalReadPolicy(IOptionsMonitor<AgentOptions> options)
{
    public Uri AuthorizeHttp(string url, string method, string? body)
    {
        if (method is not ("GET" or "HEAD") || body is not null
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw Denied("Only GET/HEAD without a body, credentials or fragment are permitted.");
        return uri;
    }

    public AgentMcpReadGrant AuthorizeMcpServer(AgentMcpServer server, string toolName)
        => (options.CurrentValue.ReadOnlyMcpTools ?? []).FirstOrDefault(g => g is not null && server.Enabled
            && g.ServerId == server.Id && g.ToolName == toolName && DateTimeOffset.TryParse(g.ServerUpdatedAt, out var revision)
            && revision.UtcDateTime == DateTime.SpecifyKind(server.UpdatedAt, DateTimeKind.Utc))
            ?? throw Denied("MCP tool has no read approval for this server revision. Re-approve after server or credential changes.");

    public void AuthorizeMcpTool(AgentMcpServer server, McpClientTool tool, JsonElement? arguments = null)
    {
        var grant = AuthorizeMcpServer(server, tool.Name);
        if (!IsReadOnly(tool) || !ContractFingerprint(tool).Equals(grant.ContractSha256, StringComparison.OrdinalIgnoreCase))
            throw Denied("MCP tool's read annotation or contract changed. Administrator review is required.");
        var schema = AgentJsonSchema.Compile(tool.JsonSchema);
        if (arguments.HasValue && !schema.Evaluate(arguments.Value).IsValid)
            throw Denied("MCP arguments do not match the approved tool schema.");
    }

    public static bool IsReadOnly(McpClientTool tool)
        => tool.ProtocolTool.Annotations?.ReadOnlyHint == true && tool.ProtocolTool.Annotations?.DestructiveHint != true;

    public static string ContractFingerprint(McpClientTool tool)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tool.ProtocolTool))));

    private static UnauthorizedAccessException Denied(string message) => new("Agent read-only policy: " + message);
}
