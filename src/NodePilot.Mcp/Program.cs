using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodePilot.Mcp.Api;
using NodePilot.Mcp.Auth;
using NodePilot.Mcp.Config;
using NodePilot.Mcp.Resources;
using NodePilot.Mcp.Tools;

[assembly: SupportedOSPlatform("windows")]

var builder = Host.CreateApplicationBuilder(args);

// stdio transport: stdout is the JSON-RPC channel. ALL logging must go to stderr or the
// protocol stream is corrupted. Clear the default (stdout) console provider and re-add on stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Connection plumbing — singletons. The NodePilotApiClient is built once from the resolved
// session (env-first; falls back to the CLI's DPAPI session + config.json).
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<McpServerConfig>();
builder.Services.AddSingleton<ApiClientFactory>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<ApiClientFactory>().Create());

// Tools are registered EXPLICITLY (never WithToolsFromAssembly) so the gated destructive
// tools can never be pulled in by an assembly scan. Safe tools first:
var mcp = builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DiscoveryTools>()
    .WithTools<WorkflowReadTools>()
    .WithTools<WorkflowEditTools>()
    .WithTools<ExecutionTools>()
    .WithTools<TelemetryTools>()
    .WithTools<SupportingDataTools>()
    .WithTools<AlertingTools>()
    .WithTools<SystemAlertingTools>()
    .WithTools<CanvasAssistantTools>()
    .WithResources<NodePilotResources>();

// Destructive/admin tools are registered ONLY when explicitly enabled — so the agent never
// even sees them in tools/list otherwise. Conditional registration (not just a runtime block)
// is the actual gate; the static check avoids building the DI graph here.
if (McpServerConfig.IsDestructiveAllowed())
    mcp.WithTools<DestructiveTools>();

await builder.Build().RunAsync();
