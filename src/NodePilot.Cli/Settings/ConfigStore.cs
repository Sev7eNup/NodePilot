using System.Text.Json;
using System.Text.Json.Serialization;
using NodePilot.Core.Clients;

namespace NodePilot.Cli.Settings;

/// <summary>
/// Plain-JSON config under %APPDATA%\NodePilot\config.json. Holds non-secret connection
/// settings only — tokens live in the shared <see cref="TokenStore"/> (DPAPI-encrypted) so a
/// config backup never carries a usable session. The CLI is the only writer; the read side
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

    /// <summary>
    /// Resolve the certificate pin for a call: CLI flag &gt; environment variable &gt; profile.
    /// A stored pin is origin-bound like the session token — it overrides hostname validation, so
    /// it must not authenticate a server it was never accepted for.
    /// </summary>
    public ResolvedTlsPin ResolveTlsThumbprint(string? cliFlag, string profile, string? server, CliConfig? config = null)
    {
        if (!string.IsNullOrWhiteSpace(cliFlag))
            return new ResolvedTlsPin(TlsPinInput.Require(cliFlag, "--tls-thumbprint"), null);

        var env = Environment.GetEnvironmentVariable(TlsThumbprintEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
            return new ResolvedTlsPin(TlsPinInput.Require(env, TlsThumbprintEnvironmentVariable), null);

        var cfg = config ?? Load();
        if (!cfg.Profiles.TryGetValue(profile, out var entry) || string.IsNullOrWhiteSpace(entry.TlsThumbprint))
            return new ResolvedTlsPin(null, null);

        var pin = TlsPinInput.Require(entry.TlsThumbprint, $"Der TLS-Pin in {ConfigPath}");
        if (!ClientSessionSecurity.HasSameServerOrigin(entry.Server, server))
        {
            return new ResolvedTlsPin(
                null,
                $"Profil-Pin gilt für {entry.Server} und wurde für {server} ignoriert.");
        }

        return new ResolvedTlsPin(pin, null);
    }

    /// <summary>Bypass switch: flag or environment variable. Deliberately not persistable.</summary>
    public static bool ResolveSkipTlsVerification(bool cliFlag)
    {
        if (cliFlag) return true;
        var value = Environment.GetEnvironmentVariable(SkipTlsVerificationEnvironmentVariable);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    public const string TlsThumbprintEnvironmentVariable = "NODEPILOT_TLS_THUMBPRINT";
    public const string SkipTlsVerificationEnvironmentVariable = "NODEPILOT_TLS_NO_VERIFY";
}

/// <summary>
/// The pin a call runs with, plus why a stored one was left out — the caller prints that reason
/// instead of failing with an unexplained certificate error.
/// </summary>
public sealed record ResolvedTlsPin(string? Value, string? IgnoredReason);
