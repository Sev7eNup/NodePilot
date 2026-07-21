using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodePilot.Cli.Settings;

/// <summary>
/// Plain-JSON config under %APPDATA%\NodePilot\config.json. Holds non-secret connection
/// settings only — tokens live in <c>Auth/TokenStore</c> (DPAPI-encrypted) so a config
/// backup never carries a usable session.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ConfigDir { get; }
    public string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public ConfigStore() : this(DefaultConfigDir()) { }

    public ConfigStore(string configDir)
    {
        ConfigDir = configDir;
        Directory.CreateDirectory(ConfigDir);
    }

    public static string DefaultConfigDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "NodePilot");
    }

    public CliConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new CliConfig();
        try
        {
            using var stream = File.OpenRead(ConfigPath);
            return JsonSerializer.Deserialize<CliConfig>(stream, JsonOptions) ?? new CliConfig();
        }
        catch (JsonException)
        {
            // Corrupt config → treat as empty rather than blocking the user. They can
            // always `np config set server …` to repair, and `np auth login` re-creates.
            return new CliConfig();
        }
    }

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

public sealed class CliConfig
{
    public string DefaultProfile { get; set; } = "default";
    public Dictionary<string, ProfileEntry> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProfileEntry
{
    public string? Server { get; set; }
}
