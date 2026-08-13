using NodePilot.Core.Interfaces;
using NodePilot.Data.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NodePilot.Api.Configuration;

/// <summary>
/// Wires <c>appsettings.runtime.json</c> into the configuration pipeline at the right
/// place — after <c>appsettings.{Env}.json</c> (Installer-Bootstrap) but before
/// EnvVars/CLI (Deployment-Policy). The host doesn't insert this source itself; we
/// have to splice it into <see cref="WebApplicationBuilder.Configuration"/> manually.
///
/// <para>Splicing is needed instead of <c>AddJsonFile()</c>-after-the-fact because
/// the default builder appends to the end (after EnvVars), which would let UI saves
/// override Env-injected secrets — that breaks Container/K8s deployments where the
/// override-file pattern is "Env wins". We want the opposite priority.</para>
/// </summary>
public static class RuntimeOverridesSetup
{
    public const string OverridesPathConfigKey = "Settings:RuntimeOverridesPath";
    public const string DefaultFilename = "appsettings.runtime.json";

    /// <summary>
    /// Insert the runtime-overrides JSON source into the builder's source list, directly
    /// after the environment-specific appsettings file (or after the base
    /// <c>appsettings.json</c> if no env-specific file is present). Returns the absolute
    /// resolved path and the bootstrap-built <see cref="ISecretProtector"/> so callers
    /// can register both in DI alongside the writer.
    ///
    /// <para>The protector is constructed from a temporary configuration snapshot that
    /// includes everything EXCEPT the override file itself — the file is what the
    /// protector will then decrypt during the host's full configuration load. This
    /// breaks the chicken-and-egg between "which protector?" and "decrypted values".</para>
    /// </summary>
    public static (string OverridesPath, ISecretProtector Protector, int MigratedFiles) AddRuntimeOverridesJson(
        this WebApplicationBuilder builder)
    {
        var resolved = ResolveOverridesPath(builder.Configuration, builder.Environment.ContentRootPath);

        // Build a snapshot identical to the host's eventual configuration EXCEPT for the
        // override file. The protector reads Secrets:* / Credentials:DpapiScope /
        // Cluster:Enabled from this snapshot — all of those keys must be addressable
        // via EnvVars OR CLI args (operators routinely pass Secrets:MasterKey via
        // --Secrets:MasterKey=... on the dotnet command line in tests / one-shot
        // recoveries), so the snapshot must include both. Without CLI args the
        // override-file decrypt path could start with the wrong protector when the
        // CLI overrides Secrets:Provider — this was caught during a security review
        // (tracked there as Finding 5).
        var commandLineArgs = Environment.GetCommandLineArgs();
        // Skip [0] which is the executable path; the rest are the actual switches.
        var cliArgs = commandLineArgs.Length > 1
            ? commandLineArgs.Skip(1).ToArray()
            : Array.Empty<string>();
        var bootstrapSnapshot = new ConfigurationBuilder()
            .SetBasePath(builder.Environment.ContentRootPath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(cliArgs)
            .Build();
        var protector = SecretProtectorBootstrapFactory.FromConfigSnapshot(bootstrapSnapshot);

        // `Otlp.Headers` only became a registered secret after the 2026 security audit. Encrypt
        // legacy plaintext in both the active file and its rollback copies before the active
        // provider ever loads it. A later Settings PUT also performs this upgrade, but startup is
        // the only deterministic migration point for installations that never revisit the UI.
        var migratedFiles = MigrateLegacyPlaintextSecrets(resolved, protector);

        var sources = builder.Configuration.Sources;
        var insertAt = FindInsertionIndex(sources, builder.Environment.EnvironmentName);
        var source = new EncryptingJsonConfigurationSource(
            resolved, protector, optional: true, reloadOnChange: true);
        sources.Insert(insertAt, source);

        return (resolved, protector, migratedFiles);
    }

    /// <summary>
    /// Encrypts every plaintext field registered by <see cref="SettingsSchema"/> in the active
    /// runtime-override file and its writer-created backups. Files that contain no legacy value
    /// are not rewritten. Each changed file is replaced from a same-directory temporary file so
    /// a crash cannot expose a half-written JSON document.
    /// </summary>
    internal static int MigrateLegacyPlaintextSecrets(string overridesPath, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(overridesPath);
        ArgumentNullException.ThrowIfNull(protector);

        var absolutePath = Path.GetFullPath(overridesPath);
        var directory = Path.GetDirectoryName(absolutePath);
        var basename = Path.GetFileName(absolutePath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(basename)) return 0;

        var candidates = new List<string>();
        if (File.Exists(absolutePath)) candidates.Add(absolutePath);
        if (Directory.Exists(directory))
        {
            candidates.AddRange(Directory.EnumerateFiles(directory, basename + ".bak.*")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        }

        var migrated = 0;
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            JsonObject root;
            try
            {
                var raw = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                root = JsonNode.Parse(raw) as JsonObject
                    ?? throw new InvalidOperationException("the JSON root is not an object");
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"Cannot securely migrate runtime-settings secrets in '{path}': {ex.Message}", ex);
            }

            var changed = false;
            foreach (var descriptor in SettingsSchema.Sections)
            {
                foreach (var secretPath in descriptor.SecretFieldPaths)
                {
                    var segments = descriptor.SectionPath
                        .Split([':', '.'], StringSplitOptions.RemoveEmptyEntries)
                        .Concat(secretPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
                        .ToArray();
                    changed |= EncryptMatchingValues(root, segments, 0, protector);
                }
            }

            if (!changed) continue;
            ReplaceAtomically(path, root);
            migrated++;
        }

        return migrated;
    }

    private static bool EncryptMatchingValues(
        JsonNode? node,
        IReadOnlyList<string> path,
        int depth,
        ISecretProtector protector)
    {
        if (node is not JsonObject obj || depth >= path.Count) return false;

        var segment = path[depth];
        if (segment == "*")
        {
            var changed = false;
            foreach (var child in obj.Select(pair => pair.Value).ToArray())
                changed |= EncryptMatchingValues(child, path, depth + 1, protector);
            return changed;
        }

        // IConfiguration keys are case-insensitive, while JsonObject lookups are not. Process
        // every casing variant so a manually-authored lower-case override cannot bypass the
        // at-rest secret policy (and so ambiguous duplicate variants fail safely at provider load).
        var matches = obj
            .Where(pair => pair.Key.Equals(segment, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (depth != path.Count - 1)
        {
            var changed = false;
            foreach (var match in matches)
                changed |= EncryptMatchingValues(match.Value, path, depth + 1, protector);
            return changed;
        }

        var encryptedAny = false;
        foreach (var match in matches)
        {
            if (match.Value is not JsonValue value
                || !value.TryGetValue(out string? plaintext)
                || string.IsNullOrEmpty(plaintext)
                || EncryptingJsonConfigurationProvider.LooksEncrypted(plaintext))
                continue;

            obj[match.Key] = EncryptingJsonConfigurationProvider.EncryptForPersist(plaintext, protector);
            encryptedAny = true;
        }
        return encryptedAny;
    }

    private static void ReplaceAtomically(string path, JsonObject root)
    {
        var temporary = path + ".migration." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporary,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            try
            {
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException
                                       or UnauthorizedAccessException)
            {
                // UNC/non-NTFS fallback. The temporary lives beside the target so the move stays
                // on one volume and inherits the same restricted directory boundary.
                File.Move(temporary, path, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Resolve the absolute file path for the runtime overrides. Order:
    /// 1) <c>Settings:RuntimeOverridesPath</c> from configuration (absolute or relative
    ///    to ContentRoot), 2) <c>{ContentRoot}/appsettings.runtime.json</c>.
    /// </summary>
    public static string ResolveOverridesPath(IConfiguration config, string contentRoot)
    {
        var configured = config[OverridesPathConfigKey];
        if (string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Path.Combine(contentRoot, DefaultFilename));

        return Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(contentRoot, configured));
    }

    /// <summary>
    /// Find the index where the runtime source should be inserted. Looks for the
    /// environment-specific JSON file by suffix; falls back to the position after
    /// the base appsettings.json; falls back to "after every JSON source" if neither
    /// is present (e.g. minimal hosts).
    /// </summary>
    internal static int FindInsertionIndex(IList<IConfigurationSource> sources, string environmentName)
    {
        var envSuffix = $"appsettings.{environmentName}.json";
        var baseSuffix = "appsettings.json";

        var envIdx = -1;
        var baseIdx = -1;
        var lastJsonIdx = -1;

        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is FileConfigurationSource fs && fs.Path is { } p)
            {
                if (p.EndsWith(envSuffix, StringComparison.OrdinalIgnoreCase)) envIdx = i;
                else if (p.EndsWith(baseSuffix, StringComparison.OrdinalIgnoreCase)) baseIdx = i;
                lastJsonIdx = i;
            }
        }

        if (envIdx >= 0) return envIdx + 1;
        if (baseIdx >= 0) return baseIdx + 1;
        if (lastJsonIdx >= 0) return lastJsonIdx + 1;
        return sources.Count;
    }

    /// <summary>
    /// Register the writer with the absolute override-file path. Save-side encryption
    /// in later PRs will resolve <see cref="ISecretProtector"/> from DI (which includes
    /// the migrating-fallback wrapper when a key rotation is in progress) — the
    /// bootstrap protector wired into the JSON source for decrypt is intentionally
    /// kept out of DI to avoid mistaking it for the regular runtime protector.
    /// </summary>
    public static IServiceCollection AddRuntimeOverridesWriter(
        this IServiceCollection services,
        string overridesPath)
    {
        services.AddSingleton(sp => new RuntimeOverridesWriter(
            overridesPath,
            sp.GetRequiredService<ILogger<RuntimeOverridesWriter>>()));
        return services;
    }
}
