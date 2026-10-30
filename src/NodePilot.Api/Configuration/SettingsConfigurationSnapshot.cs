using System.Text;
using System.Text.Json.Nodes;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Configuration;

/// <summary>One runtime document and its effective provider chain for settings reads and validation.</summary>
internal sealed class SettingsConfigurationSnapshot : IDisposable
{
    private readonly JsonObject _runtime;
    private readonly string _path;
    private readonly ISecretProtector _protector;

    private SettingsConfigurationSnapshot(IConfigurationRoot source, JsonObject runtime, string path,
        ISecretProtector protector)
    {
        _runtime = runtime;
        _path = path;
        _protector = protector;
        using var runtimeProvider = new EncryptingJsonConfigurationProvider(
            new EncryptingJsonConfigurationSource(path, protector, optional: true, reloadOnChange: false));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(runtime.ToJsonString()));
        runtimeProvider.Load(stream);
        var replacement = new SnapshotProvider(runtimeProvider, EffectiveSourceDetector.SourceRuntime);
        var sourceProviders = source.Providers.ToList();
        // The host registers prefixed environment providers ahead of the appsettings files; only
        // an environment or command-line provider after them ranks above the runtime file.
        var lastAppsettings = sourceProviders.FindLastIndex(p => EffectiveSourceDetector.Classify(p)
            is EffectiveSourceDetector.SourceAppsettings or EffectiveSourceDetector.SourceProduction);
        var providers = new List<IConfigurationProvider>();
        var replaced = false;
        for (var i = 0; i < sourceProviders.Count; i++)
        {
            var provider = sourceProviders[i];
            var origin = EffectiveSourceDetector.Classify(provider);
            if (origin == EffectiveSourceDetector.SourceRuntime)
            {
                if (!replaced) providers.Add(replacement);
                replaced = true;
            }
            else
            {
                if (!replaced && i > lastAppsettings
                    && origin is EffectiveSourceDetector.SourceEnv or EffectiveSourceDetector.SourceCli)
                {
                    providers.Add(replacement);
                    replaced = true;
                }
                providers.Add(new SnapshotProvider(provider, origin));
            }
        }
        if (!replaced) providers.Add(replacement);
        Configuration = new ConfigurationRoot(providers);
        Adapters = SettingsSectionAdapters.CreateSnapshot(Configuration, protector);
    }

    public IConfigurationRoot Configuration { get; }
    public ISettingsSectionAdapterRegistry Adapters { get; }

    public static SettingsConfigurationSnapshot Capture(IConfigurationRoot source,
        RuntimeOverridesWriter writer, ISecretProtector protector)
        => new(source, writer.ReadOrEmpty(), writer.OverridesPath, protector);

    public JsonObject? ReadSection(string path) => RuntimeOverridesWriter.ProjectSection(_runtime, path);

    public SettingsConfigurationSnapshot WithSection(string path, JsonObject section)
    {
        var candidate = _runtime.DeepClone().AsObject();
        RuntimeOverridesWriter.ApplySectionInternal(candidate, path, section.DeepClone().AsObject());
        return new(Configuration, candidate, _path, _protector);
    }

    public void Dispose() => (Configuration as IDisposable)?.Dispose();

    internal sealed class SnapshotProvider : ConfigurationProvider
    {
        public string Source { get; }

        public SnapshotProvider(IConfigurationProvider provider, string source)
        {
            Source = source;
            Copy(provider, null);
        }

        private void Copy(IConfigurationProvider provider, string? parent)
        {
            foreach (var child in provider.GetChildKeys([], parent).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = parent is null ? child : $"{parent}:{child}";
                if (provider.TryGet(key, out var value)) Data[key] = value;
                Copy(provider, key);
            }
        }
    }
}
