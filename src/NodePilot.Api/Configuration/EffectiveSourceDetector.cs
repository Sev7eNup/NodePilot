using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace NodePilot.Api.Configuration;

/// <summary>
/// Identifies which provider in the configuration chain currently supplies the value
/// for a given key. The Admin Settings API uses it so the UI can render
/// env-overridden fields as read-only.
///
/// <para>The result is an approximation: env and CLI providers carry no per-key source
/// map, so classification walks the providers in reverse order (last wins, matching
/// configuration lookup) and reports the class of the first provider that defines the
/// key. When several providers define it, only the last one is reported. The UI only
/// needs to know whether the field is read-only, so that is accurate enough.</para>
///
/// <para>Returned source tokens (lowercase, stable for API contract):</para>
/// <list type="bullet">
///   <item><c>"default"</c> — no provider has the key (value is the bound POCO default)</item>
///   <item><c>"appsettings"</c> — base <c>appsettings.json</c></item>
///   <item><c>"production"</c> — <c>appsettings.{Env}.json</c></item>
///   <item><c>"runtime"</c> — the UI-managed override file (<c>appsettings.runtime.json</c>)</item>
///   <item><c>"json"</c> — any other JSON source</item>
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
            if (ProviderDefines(provider, key))
                return Classify(provider);
        }
        return SourceDefault;
    }

    /// <summary>
    /// Source token of the first provider other than the runtime-overrides file that supplies any
    /// of <paramref name="keys"/>, or <c>null</c> when only the runtime file (or nothing) does.
    ///
    /// <para>Answers whether removing the runtime entry would make the value disappear.
    /// <see cref="Detect"/> cannot: the runtime file sits above the base config, so once a value
    /// has
    /// been saved through the UI it reports <c>runtime</c> even though the underlying
    /// <c>appsettings.json</c> entry would resurface once the runtime entry is dropped.</para>
    /// </summary>
    public static string? DetectNonRuntimeSource(IConfigurationRoot root, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(keys);

        var keyList = keys as IReadOnlyList<string> ?? keys.ToList();
        foreach (var provider in root.Providers.Reverse())
        {
            var source = Classify(provider);
            if (source == SourceRuntime) continue;
            foreach (var key in keyList)
            {
                if (ProviderDefines(provider, key)) return source;
            }
        }
        return null;
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

    private static bool ProviderDefines(IConfigurationProvider provider, string key)
        => provider.TryGet(key, out _)
           || provider.GetChildKeys([], key).Any();

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
