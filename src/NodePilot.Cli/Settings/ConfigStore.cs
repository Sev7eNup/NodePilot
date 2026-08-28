using System.Text.Json;
using System.Text.Json.Serialization;
using NodePilot.Core.Clients;

namespace NodePilot.Cli.Settings;

/// <summary>
/// Plain-JSON config under %APPDATA%\NodePilot\config.json. Holds non-secret connection
/// settings only — tokens live in <c>Auth/TokenStore</c> (DPAPI-encrypted) so a config
/// backup never carries a usable session. The CLI is the only writer; the read side
/// (path, <c>Load</c>, <c>CliConfig</c>) lives in <see cref="ClientConfigStore"/> so the
/// MCP server reads exactly the same file the same way.
/// </summary>
public sealed class ConfigStore : ClientConfigStore
{
    // Write-side only: indentation and null-skipping shape the emitted file. Reading goes
    // through the base store, which needs neither.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ConfigStore() : base(DefaultConfigDir()) { }

    public ConfigStore(string configDir) : base(configDir) { }

    public void Save(CliConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    public string ResolveProfileName(string? requested, CliConfig? config = null)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();
        var cfg = config ?? Load();
        return string.IsNullOrWhiteSpace(cfg.DefaultProfile) ? "default" : cfg.DefaultProfile;
    }

    /// <summary>
    /// Resolve the server URL a command should hit, honouring precedence:
    /// CLI flag &gt; environment variable &gt; named profile &gt; default profile.
    /// </summary>
    public string? ResolveServer(string? cliFlag, string profile, CliConfig? config = null)
    {
        if (!string.IsNullOrWhiteSpace(cliFlag)) return cliFlag.Trim();
        var env = Environment.GetEnvironmentVariable("NODEPILOT_SERVER");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        var cfg = config ?? Load();
        if (cfg.Profiles.TryGetValue(profile, out var p) && !string.IsNullOrWhiteSpace(p.Server))
            return p.Server;
        return null;
    }
}
