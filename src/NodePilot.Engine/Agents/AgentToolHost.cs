using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Agents;

public sealed class AgentToolHost(AgentTargetFactory targets, AgentMcpClientFactory mcp,
    AgentRunDatabase database, IServiceScopeFactory scopes, RestApiHttpClientProvider http, AgentExternalReadPolicy reads)
{
    public async Task<Session> OpenAsync(AgentDefinition definition, StepExecutionContext context,
        Guid runId, AgentArtifactStore artifacts, AgentExecutionGate.Lease lease, CancellationToken ct, int outputLimit = 16_000)
    {
        var session = new Session();
        try
        {
            if (AgentConfiguration.RequiresTarget(definition))
            {
                session.Target = await targets.CreateAsync(definition, context, ct);
                session.Owned.Add(session.Target);
            }
            foreach (var selection in definition.Tools)
            {
                if (selection.Name == "mcp")
                {
                    var registered = await ReadMcpServerAsync(selection.McpServerId!.Value, ct);
                    reads.AuthorizeMcpServer(registered, selection.McpToolName!);
                    var client = await mcp.ConnectAsync(registered, ct);
                    session.Owned.Add(client);
                    var remoteTools = await client.ListToolsAsync(cancellationToken: ct);
                    var selected = remoteTools.SingleOrDefault(t => t.Name == selection.McpToolName)
                        ?? throw new ArgumentException("Selected MCP tool is no longer available.");
                    reads.AuthorizeMcpTool(registered, selected);
                    var name = "mcp_" + session.Tools.Count;
                    session.Tools.Add(new AgentTool(name, $"{selection.McpToolName}: {selected.Description}", selected.JsonSchema, async (input, token) =>
                    {
                        var current = await ReadMcpServerAsync(registered.Id, token);
                        if (current.UpdatedAt != registered.UpdatedAt)
                            throw new UnauthorizedAccessException("MCP connection changed during this run.");
                        reads.AuthorizeMcpServer(current, selection.McpToolName!);
                        var fresh = (await client.ListToolsAsync(cancellationToken: token)).SingleOrDefault(t => t.Name == selection.McpToolName)
                            ?? throw new UnauthorizedAccessException("MCP tool was removed.");
                        reads.AuthorizeMcpTool(current, fresh, input);
                        var arguments = input.Deserialize<Dictionary<string, object?>>()!;
                        var result = await client.CallToolAsync(selection.McpToolName!, arguments, cancellationToken: token);
                        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                        if (result.StructuredContent is not null) text += "\n" + result.StructuredContent.Value.GetRawText();
                        if (result.IsError == true)
                            throw new AgentToolExecutionException("mcp_tool_failed", text);
                        return text;
                    }));
                    continue;
                }
                session.Tools.Add(CreateNative(selection, definition, context, session.Target, artifacts, lease));
            }
            session.Tools.AddRange(await AgentSkillTools.CreateAsync(database, definition, session.Target, runId, ct, outputLimit));
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }

    private AgentTool CreateNative(AgentToolSelection selection, AgentDefinition definition, StepExecutionContext context,
        AgentTarget? target, AgentArtifactStore artifacts, AgentExecutionGate.Lease lease)
    {
        var name = selection.Name;
        var schema = name switch
        {
            "powershell" or "cmd" or "bash" => Schema(["command"], ("command", "string")),
            "files_list" => Schema(["path"], ("path", "string"), ("filter", "string")),
            "files_read" => Schema(["path"], ("path", "string"), ("offset", "integer")),
            "files_write" => Schema(["path", "content"], ("path", "string"), ("content", "string")),
            "files_search" => JsonSerializer.SerializeToElement(new
            {
                type = "object", properties = new
                {
                    path = new { type = "string" },
                    query = new { type = "string", minLength = 1, maxLength = 1024, description = "Nonempty literal search text; not a regular expression. Use files_read for an unfiltered excerpt." },
                    order = new { type = "string", @enum = new[] { "first", "last" } }
                },
                required = new[] { "path", "query" }, additionalProperties = false
            }),
            "logs_collect" => Schema(["path"], ("path", "string")),
            "logs_search" => Schema(["artifactId", "query"], ("artifactId", "string"), ("query", "string"), ("maxMatches", "integer")),
            "http_request" => JsonSerializer.SerializeToElement(new {
                type = "object", properties = new {
                    url = new { type = "string", minLength = 1, description = "Absolute HTTP(S) URL to read." },
                    method = new { type = "string", minLength = 1, description = "GET or HEAD; omit for GET." }
                }, required = new[] { "url" }, additionalProperties = false
            }),
            "workflow_run" => Schema(["workflowId"], ("workflowId", "string"), ("parameters", "object")),
            _ => throw new ArgumentException("Unsupported agent tool.")
        };
        var description = Description(name, selection);
        if (target is not null && name is "powershell" or "cmd" or "bash" or "files_list" or "files_read" or "files_search" or "logs_collect")
            description = $"Bound target: {target.Hostname} (machine ID {definition.TargetMachineId}). Every call executes on this target only, regardless of the assignment's wording. " + description;
        if (name == "powershell") description += AgentPermissionPolicy.DescribePowerShellReads()
            + " For CIM property projection use separate literal names: -Property Name,State; never -Property 'Name,State' (a single invalid property)."
            + " Inline commands also support literal variable bindings (substituted before execution), single-class SELECT queries on approved CIM classes, ErrorAction Stop/Continue/SilentlyContinue/Ignore, Where-Object -Property Status -EQ -Value Running, ConvertFrom-Json and ConvertFrom-Csv. No predicate script blocks or mutable script state. Skill scripts still require literal commands with bound parameters.";
        if (name == "cmd") description += " Local Windows diagnosis also supports ipconfig (/all or /displaydns), netstat (-a/-n/-o/-r/-b, combinable), sc query/queryex/qc/qdescription/qfailure with an optional literal service name, tasklist (/svc,/v,/nh), systeminfo without arguments and exactly netsh winhttp show proxy. Mutation switches and remote server arguments are denied.";
        if (name == "bash") description += " Supported executables: cat, head, tail, wc, grep, ls, pwd, uname, whoami, hostname, stat, sha256sum, echo, du, df, ps, id, readlink. Read pipelines such as cat '/c/log.txt' | grep ERROR | head -n 20 are supported. uname accepts short read flags; hostname/whoami/pwd take no arguments. No filesystem sync, command substitution, output redirection or conditional command chaining.";
        if (name == "powershell") description += " Get-Service Status and StartType are returned as named strings. IIS reads: Get-Website -Name 'site'; Get-WebBinding -Name 'site'; Get-WebAppPoolState -Name 'pool'. For all pools use -Name '*'. The literal command netsh winhttp show proxy reads the effective local WinHTTP proxy. These queries never change configuration. Resolve-DnsName is a DNS-only query, not proof of application name resolution: also read C:\\Windows\\System32\\drivers\\etc\\hosts and Get-DnsClientCache for a name-resolution investigation. Server overrides are denied. The tool Target is a WinRM transport address; it may differ from the machine's internal service addresses. Confirm identity with Get-ComputerInfo and Get-NetIPAddress before treating different addresses as a fault.";
        if (selection.AllowedPaths.Length > 0) description += " Allowed paths: " + string.Join(", ", selection.AllowedPaths);
        if (target is not null) description += " Target: " + target.Hostname + ".";
        return new AgentTool(name, description, schema, async (input, ct) =>
        {
            var checkedCommand = AgentPermissionPolicy.AuthorizeTool(name, input);
            string Required(string key) => input.GetProperty(key).GetString() ?? throw new ArgumentException($"{key} is required.");
            string? Optional(string key) => input.TryGetProperty(key, out var value) ? value.GetString() : null;
            string PathArgument() => AgentFileTools.ValidatePath(Required("path"), selection.AllowedPaths);
            switch (name)
            {
                case "powershell": case "cmd": case "bash":
                    return await target!.ExecuteAsync(AgentProcessScript.Build(name, checkedCommand!,
                        definition.WorkingDirectory, definition.BashPath), ct, 310);
                case "files_list":
                    return await AgentFileTools.ListAsync(target!, PathArgument(), Optional("filter") ?? "*", ct);
                case "files_read":
                    return await AgentFileTools.ReadAsync(target!, PathArgument(), input.TryGetProperty("offset", out var offset) ? offset.GetInt64() : 0, ct);
                case "files_write":
                    await AgentFileTools.WriteAsync(target!, PathArgument(), Required("content"), ct);
                    return "File written.";
                case "logs_collect":
                {
                    var collected = await AgentFileTools.CollectAsync(target!, artifacts, PathArgument(), ct);
                    return JsonSerializer.Serialize(new { artifactId = collected.Id, collected.Source, collected.Length });
                }
                case "files_search":
                    return await AgentFileTools.SearchAsync(target!, PathArgument(), Required("query"), ct, Optional("order") ?? "first");
                case "logs_search":
                    return await artifacts.SearchAsync(Required("artifactId"), Required("query"),
                        input.TryGetProperty("maxMatches", out var count) ? count.GetInt32() : 20, ct);
                case "http_request":
                    return await RequestAsync(selection, Required("url"), Optional("method") ?? "GET", Optional("body"), ct);
                case "workflow_run":
                {
                    if (!Guid.TryParse(Required("workflowId"), out var id) || !selection.WorkflowIds.Contains(id))
                        throw new UnauthorizedAccessException("Workflow is not selected for this agent.");
                    if (!await database.UseAsync(db => db.Workflows.AsNoTracking().AnyAsync(w => w.Id == id && w.IsEnabled
                        && w.CheckedOutByUserId == null && w.PublishedByUserId != null, ct), ct))
                        throw new UnauthorizedAccessException("The selected workflow is not published and enabled.");
                    var config = JsonSerializer.SerializeToElement(new
                    {
                        workflowNameOrId = id.ToString(), waitForCompletion = true, timeoutSeconds = 1800,
                        parameters = input.TryGetProperty("parameters", out var parameters) ? parameters : JsonSerializer.SerializeToElement(new { })
                    });
                    var childContext = new StepExecutionContext
                    {
                        WorkflowExecutionId = context.WorkflowExecutionId, StepId = context.StepId, StepLabel = context.StepLabel,
                        Variables = context.Variables, PropagateChildCancellation = true
                    };
                    using var readScope = AgentReadOnlyWorkflowScope.Enter();
                    using var workflowScope = scopes.CreateScope();
                    var workflow = workflowScope.ServiceProvider.GetRequiredService<StartWorkflowActivity>();
                    var result = await lease.WhileReleasedAsync(() => workflow.ExecuteAsync(childContext, config, ct), ct);
                    return JsonSerializer.Serialize(new { result.Success, result.Output, result.ErrorOutput, result.OutputParameters });
                }
                default: throw new InvalidOperationException("Unsupported agent tool.");
            }
        });
    }

    private async Task<string> RequestAsync(AgentToolSelection selection, string url, string method, string? body, CancellationToken ct)
    {
        method = method.ToUpperInvariant();
        var uri = reads.AuthorizeHttp(url, method, body);
        if (selection.AllowedHosts.Length > 0 && !selection.AllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("URL is outside this tool's allowed hosts.");
        var config = JsonSerializer.SerializeToElement(new { proxyMode = "default" });
        http.ValidateDestinationPolicy(config, uri);
        using var client = http.GetClient(config);
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidOperationException("Agent HTTP tools do not follow redirects. Select the destination explicitly.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[16_001];
        var bytes = 0;
        while (bytes < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(bytes), ct);
            if (read == 0) break;
            bytes += read;
        }
        return JsonSerializer.Serialize(new { statusCode = (int)response.StatusCode,
            body = Encoding.UTF8.GetString(buffer, 0, Math.Min(bytes, 16_000)), truncated = bytes > 16_000 });
    }

    private async Task<AgentMcpServer> ReadMcpServerAsync(Guid id, CancellationToken ct)
        => await database.UseAsync(db => db.AgentMcpServers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.Enabled, ct), ct)
            ?? throw new UnauthorizedAccessException("MCP server is missing or disabled.");

    private static JsonElement Schema(string[] required, params (string Name, string Type)[] fields)
        => JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = fields.ToDictionary(f => f.Name, f => new { type = f.Type }),
            required, additionalProperties = false
        });

    private static string Description(string name, AgentToolSelection selection) => name switch
    {
        "files_list" => "List up to 100 immediate entries in an allowed directory; optional wildcard filter, no recursive traversal. Inspect relevant returned subdirectories explicitly. An empty filtered result does not prove that the directory tree contains no logs. LastWriteTimeUtc is an exact ISO-8601 UTC file modification timestamp, not a log-event timestamp or the timezone of the file contents.",
        "files_read" => "Read an 8192-byte text excerpt at a byte offset; returns nextOffset.",
        "files_write" => "Currently blocked by the agent read-only policy; file writing is not permitted.",
        "files_search" => "Search one allowed file on the target without collecting it; returns up to 20 original source lines and bounded excerpts with a truncation indicator. Set order=last for the last matching lines when diagnosing current appended logs; default order=first returns earliest file matches. File order is not event-time order. Timestamps without a source offset or verified timezone remain unspecified.",
        "logs_collect" => "Collect one allowed log file in the shared run workspace, up to 250 MB total. Returns artifactId.",
        "logs_search" => "Search a collected artifact by text and return matching original source lines; shared across team members. Collection does not assign a timezone to source timestamps.",
        "http_request" => "Read HTTP(S) using GET (default) or HEAD, without a body or redirects. Network protection applies. Optional host restriction: " + string.Join(", ", selection.AllowedHosts),
        "workflow_run" => "Run a selected published workflow under enforced read-only step checks, including synchronous descendants. Unsupported or writing steps are rejected. Allowed IDs: " + string.Join(", ", selection.WorkflowIds),
        _ => "Execute a checked read-only " + name + " command. Only supported commands and parameters are permitted. No writes, dynamic code, redirection or subprocesses. PowerShell supports approved queries (CIM classes are restricted) and selection/formatting pipelines. For local network diagnosis: Get-NetConnectionProfile; Get-NetFirewallProfile; Get-NetFirewallRule with Name/DisplayName or Enabled/Direction/Action filters, always local ActiveStore. A Get-NetFirewallRule query may pipe directly to Get-NetFirewallPortFilter, Get-NetFirewallAddressFilter, Get-NetFirewallApplicationFilter or Get-NetFirewallServiceFilter, then Select-Object/ConvertTo-Json. No CimSession, remote PolicyStore, predicate script blocks or calculated expressions. CMD accepts one literal read command; Bash also accepts pipelines of checked reads."
    };

    public sealed class Session : IAsyncDisposable
    {
        public AgentTarget? Target { get; internal set; }
        public List<AgentTool> Tools { get; } = [];
        internal List<IAsyncDisposable> Owned { get; } = [];
        public async ValueTask DisposeAsync()
        {
            List<Exception>? errors = null;
            foreach (var item in Owned.AsEnumerable().Reverse())
                try { await item.DisposeAsync(); } catch (Exception ex) { (errors ??= []).Add(ex); }
            if (errors is not null) throw new AggregateException("Agent tool cleanup failed.", errors);
        }
    }
}
