using Microsoft.Extensions.Configuration;

namespace NodePilot.Engine.Security;

/// <summary>Reads an entire list from its highest-priority declaring configuration provider.</summary>
public static class ProviderAtomicList
{
    public static List<T>? Read<T>(IConfiguration configuration, string path)
    {
        if (configuration is not IConfigurationRoot root)
            return configuration.GetSection(path).Get<List<T>>();

        foreach (var provider in root.Providers.Reverse())
        {
            var declared = provider.TryGet(path, out var scalar);
            var values = ReadValues(provider, path, "").ToArray();
            if (!declared && values.Length == 0) continue;
            if (declared && (!string.IsNullOrWhiteSpace(scalar) || values.Length != 0))
                throw new InvalidOperationException($"{path} must be an unambiguous configuration list.");

            using var selected = new ConfigurationManager();
            selected.AddInMemoryCollection(values);
            return selected.Get<List<T>>() ?? [];
        }
        return null;
    }

    private static IEnumerable<KeyValuePair<string, string?>> ReadValues(
        IConfigurationProvider provider, string path, string relative)
    {
        foreach (var child in provider.GetChildKeys([], path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var childPath = $"{path}:{child}";
            var childRelative = relative.Length == 0 ? child : $"{relative}:{child}";
            if (provider.TryGet(childPath, out var value))
                yield return new(childRelative, value);
            foreach (var descendant in ReadValues(provider, childPath, childRelative))
                yield return descendant;
        }
    }
}
