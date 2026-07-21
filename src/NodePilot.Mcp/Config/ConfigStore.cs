using System.Text.Json;

namespace NodePilot.Mcp.Config;

/// <summary>
/// Reads the same <c>%APPDATA%\NodePilot\config.json</c> the <c>np</c> CLI writes, so the
/// MCP server can fall back to a CLI-configured profile server URL. Read-only here — the
/// MCP server never writes config; the operator manages it via <c>np config</c>.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
        if (!File.Exists(ConfigPath)) return new CliConfig();
        try
        {
            using var stream = File.OpenRead(ConfigPath);
            return JsonSerializer.Deserialize<CliConfig>(stream, JsonOptions) ?? new CliConfig();
        }
        catch (JsonException)
        {
            return new CliConfig();
        }
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
