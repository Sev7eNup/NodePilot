using System.Text.Json;

namespace NodePilot.Core.Clients;

/// <summary>
/// Reads the plain-JSON config at <c>%APPDATA%\NodePilot\config.json</c> that both HTTP-only
/// clients share: the <c>np</c> CLI owns it (its <c>ConfigStore</c> adds the write side), the
/// <c>nodepilot-mcp</c> server only reads it to fall back to a CLI-configured profile server.
/// Non-secret connection settings only; tokens live in the DPAPI session store, so a config
/// backup never carries a usable session.
/// </summary>
public class ClientConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ConfigDir { get; }
    public string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public ClientConfigStore() : this(DefaultConfigDir()) { }

    public ClientConfigStore(string configDir)
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
            // A corrupt config reads as empty rather than blocking the user: `np config set
            // server ...` repairs it and `np auth login` re-creates it.
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
