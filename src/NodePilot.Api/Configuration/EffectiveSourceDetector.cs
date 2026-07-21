using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace NodePilot.Api.Configuration;

/// <summary>
/// Identifies which provider in the configuration chain currently supplies the value
/// for a given key — used by the Admin Settings API so the UI can render
/// env-overridden fields as read-only.
///
/// <para><b>Deliberate imprecision:</b> Env/CLI providers don't carry a per-key source
/// map. We classify by walking the providers in reverse order (last-wins, same as
/// configuration lookup) and report the first provider's class. Edge case: two JSON
/// providers with the same value will report the latest-source even though the
/// earlier one would still match — the UI only cares about read-only-ness, so the
/// approximation is fine.</para>
///
/// <para>Returned source tokens (lowercase, stable for API contract):</para>
/// <list type="bullet">
///   <item><c>"default"</c> — no provider has the key (value is the bound POCO default)</item>
///   <item><c>"appsettings"</c> — base <c>appsettings.json</c></item>
///   <item><c>"production"</c> — <c>appsettings.{Env}.json</c></item>
///   <item><c>"runtime"</c> — the UI-managed override file (<c>appsettings.runtime.json</c>)</item>
///   <item><c>"json"</c> — some other JSON source we didn't recognise</item>
///   <item><c>"env"</c> — Environment-variable provider</item>
///   <item><c>"cli"</c> — Command-line argument provider</item>
///   <item><c>"user-secrets"</c> — User secrets provider</item>
///   <item><c>"unknown"</c> — fallback when the provider type is none of the above</item>
/// </list>
/// </summary>
public static class EffectiveSourceDetector
{
    public const string SourceDefault = "default";
    public const string SourceAppsettings = "appsettings";
    public const string SourceProduction = "production";
    public const string SourceRuntime = "runtime";
    public const string SourceJson = "json";
    public const string SourceEnv = "env";
    public const string SourceCli = "cli";
    public const string SourceUserSecrets = "user-secrets";
    public const string SourceUnknown = "unknown";

    /// <summary>
    /// Compute the effective source for a single configuration key.
    /// </summary>
    public static string Detect(IConfigurationRoot root, string key)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key must not be empty.", nameof(key));

        // Reverse iteration mirrors configuration lookup semantics: last provider wins.
        foreach (var provider in root.Providers.Reverse())
        {
            if (provider.TryGet(key, out _))
                return Classify(provider);
        }
        return SourceDefault;
    }

    /// <summary>
    /// Detect sources for a set of keys in one pass.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DetectMany(IConfigurationRoot root, IEnumerable<string> keys)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys) map[k] = Detect(root, k);
        return map;
    }

    private static string Classify(IConfigurationProvider provider)
    {
        if (provider is EncryptingJsonConfigurationProvider) return SourceRuntime;

        if (provider is JsonConfigurationProvider json)
        {
            var path = json.Source.Path ?? string.Empty;
            if (path.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase)) return SourceAppsettings;
            if (path.Contains("appsettings.", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith("appsettings.runtime.json", StringComparison.OrdinalIgnoreCase))
                return SourceProduction;
            if (path.EndsWith("appsettings.runtime.json", StringComparison.OrdinalIgnoreCase)) return SourceRuntime;
            return SourceJson;
        }

        if (provider is EnvironmentVariablesConfigurationProvider) return SourceEnv;
        if (provider is CommandLineConfigurationProvider) return SourceCli;
        if (provider is JsonConfigurationProvider) return SourceJson;
        if (provider.GetType().Name.Contains("UserSecret", StringComparison.OrdinalIgnoreCase)) return SourceUserSecrets;
        if (provider is FileConfigurationProvider) return SourceJson;

        return SourceUnknown;
    }
}
