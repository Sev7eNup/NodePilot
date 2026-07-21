using System.Runtime.Versioning;
using NodePilot.Mcp.Auth;

namespace NodePilot.Mcp.Config;

/// <summary>
/// Resolves how the headless MCP server connects to the NodePilot REST API. Precedence
/// (env-first, since an MCP server launched from <c>.mcp.json</c> cannot prompt):
/// <list type="bullet">
///   <item>Server: <c>NODEPILOT_MCP_SERVER</c> &gt; <c>NODEPILOT_SERVER</c> &gt; CLI config.json profile.</item>
///   <item>Profile: <c>NODEPILOT_MCP_PROFILE</c> &gt; <c>NODEPILOT_PROFILE</c> &gt; CLI default &gt; <c>"default"</c>.</item>
///   <item>Token: <c>NODEPILOT_MCP_TOKEN</c> (raw bearer, CI/headless escape) &gt; DPAPI <see cref="TokenStore"/>.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class McpServerConfig
{
    private readonly ConfigStore _config;
    private readonly TokenStore _tokens;

    public McpServerConfig(ConfigStore config, TokenStore tokens)
    {
        _config = config;
        _tokens = tokens;
    }

    /// <summary>Destructive/admin tools (delete, force-unlock, cancel-all) are only registered when this is true.</summary>
    public bool AllowDestructive => IsDestructiveAllowed();

    /// <summary>Static so Program.cs can decide registration without building the full DI graph.</summary>
    public static bool IsDestructiveAllowed()
        => IsTruthy(Environment.GetEnvironmentVariable("NODEPILOT_MCP_ALLOW_DESTRUCTIVE"));

    public SessionContext Resolve()
    {
        var profile = FirstNonEmpty(
            Environment.GetEnvironmentVariable("NODEPILOT_MCP_PROFILE"),
            Environment.GetEnvironmentVariable("NODEPILOT_PROFILE"));

        var cliConfig = _config.Load();
        if (string.IsNullOrWhiteSpace(profile))
            profile = string.IsNullOrWhiteSpace(cliConfig.DefaultProfile) ? "default" : cliConfig.DefaultProfile;

        var server = FirstNonEmpty(
            Environment.GetEnvironmentVariable("NODEPILOT_MCP_SERVER"),
            Environment.GetEnvironmentVariable("NODEPILOT_SERVER"));
        if (string.IsNullOrWhiteSpace(server)
            && cliConfig.Profiles.TryGetValue(profile, out var p) && !string.IsNullOrWhiteSpace(p.Server))
            server = p.Server;

        // Token: raw env bearer (no refresh) wins; otherwise the DPAPI session for this profile.
        var rawToken = Environment.GetEnvironmentVariable("NODEPILOT_MCP_TOKEN");
        string? token = null;
        var usesRefreshableSession = false;
        if (!string.IsNullOrWhiteSpace(rawToken))
        {
            token = rawToken.Trim();
        }
        else
        {
            var session = _tokens.Load(profile);
            if (session is not null && !string.IsNullOrWhiteSpace(session.Token))
            {
                server ??= session.Server; // fall back to the server the session was minted against
                if (SessionContext.HasSameServerOrigin(session.Server, server))
                {
                    token = session.Token;
                    usesRefreshableSession = true;
                }
            }
        }

        return new SessionContext(server, profile, token, usesRefreshableSession);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static bool IsTruthy(string? value)
        => value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
}
