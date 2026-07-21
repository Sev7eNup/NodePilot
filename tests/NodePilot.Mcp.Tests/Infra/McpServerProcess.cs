using ModelContextProtocol.Client;

namespace NodePilot.Mcp.Tests.Infra;

/// <summary>
/// Launches the REAL built <c>nodepilot-mcp.exe</c> as a child process and connects an MCP
/// client to it over stdio — the most faithful test of the full stack (registration,
/// serialization, transport). Env vars point the server at a WireMock API + a raw token.
/// </summary>
public static class McpServerProcess
{
    public static async Task<McpClient> ConnectAsync(string apiUrl, string? token, CancellationToken ct, bool allowDestructive = false)
    {
        var exe = LocateServerExe();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "nodepilot-mcp-test",
            Command = exe,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["NODEPILOT_MCP_SERVER"] = apiUrl,
                ["NODEPILOT_MCP_TOKEN"] = token,
                // Explicit value (not null) so we override any inherited flag deterministically.
                ["NODEPILOT_MCP_ALLOW_DESTRUCTIVE"] = allowDestructive ? "true" : "false",
            },
        });

        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    private static string LocateServerExe()
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var tfm = baseDir.Name;                       // net10.0-windows
        var config = baseDir.Parent?.Name ?? "Debug"; // Debug | Release

        var repoRoot = FindRepoRoot(baseDir)
            ?? throw new InvalidOperationException("Could not locate repo root (NodePilot.slnx).");

        var exe = Path.Combine(repoRoot, "src", "NodePilot.Mcp", "bin", config, tfm, "nodepilot-mcp.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException($"Built MCP server not found at {exe}. Build the solution first.", exe);
        return exe;
    }

    private static string? FindRepoRoot(DirectoryInfo? dir)
    {
        for (; dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        return null;
    }
}
