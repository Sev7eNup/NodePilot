using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Agents;

public sealed class AgentMcpClientFactory(NodePilotDbContext db, ISecretProtector protector,
    RestApiHttpClientProvider httpClients, ILoggerFactory loggerFactory)
{
    public async Task<McpClient> ConnectAsync(Guid serverId, CancellationToken ct)
    {
        var server = await db.AgentMcpServers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == serverId && x.Enabled, ct)
            ?? throw new InvalidOperationException("The selected MCP server is missing or disabled.");
        return await ConnectAsync(server, ct);
    }

    internal async Task<McpClient> ConnectAsync(AgentMcpServer server, CancellationToken ct)
    {
        var secrets = server.ProtectedSecrets is null ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(server.ProtectedSecrets))!;
        IClientTransport transport;
        HttpClient? ownedHttp = null;
        if (server.Transport == "stdio")
        {
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            foreach (var pair in secrets) environment[pair.Key] = pair.Value;
            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name, Command = server.Command!,
                Arguments = JsonSerializer.Deserialize<string[]>(server.ArgumentsJson) ?? [],
                InheritEnvironmentVariables = false, EnvironmentVariables = environment,
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            }, loggerFactory);
        }
        else if (server.Transport == "streamableHttp")
        {
            var uri = new Uri(server.Endpoint!, UriKind.Absolute);
            var config = JsonSerializer.SerializeToElement(new { proxyMode = "default" });
            httpClients.ValidateDestinationPolicy(config, uri);
            var http = ownedHttp = httpClients.GetClient(config);
            transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Name, Endpoint = uri, TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = secrets, ConnectionTimeout = TimeSpan.FromSeconds(30), MaxReconnectionAttempts = 0
            }, http, loggerFactory, ownsHttpClient: true);
        }
        else throw new InvalidOperationException("Unknown MCP transport.");
        try
        {
            return await McpClient.CreateAsync(transport, new McpClientOptions
            { ClientInfo = new Implementation { Name = "NodePilot Agents", Version = "1.0" } }, loggerFactory, ct);
        }
        catch
        {
            if (transport is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (transport is IDisposable disposable) disposable.Dispose();
            ownedHttp?.Dispose();
            throw;
        }
    }

    public async Task EnsureEnabledAsync(Guid serverId, CancellationToken ct)
    {
        if (!await db.AgentMcpServers.AsNoTracking().AnyAsync(x => x.Id == serverId && x.Enabled, ct))
            throw new UnauthorizedAccessException("The MCP server has been disabled or removed.");
    }
}
