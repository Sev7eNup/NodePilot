using System.Globalization;
using System.Text.Json;

namespace NodePilot.Api.Configuration;

/// <summary>
/// Reads a GUID list from one configuration provider instead of from IConfiguration's merged
/// child view. Array indices are not replacement values in Microsoft.Extensions.Configuration:
/// a higher-priority one-element array otherwise leaves lower-priority indices 1..N visible.
/// Security allow-lists must instead use the complete value from the highest-priority provider
/// that declares the section. An explicitly empty JSON array is represented by that provider as
/// an exact key with a null value and therefore acts as a deny-all tombstone.
/// </summary>
internal static class ProviderAtomicGuidList
{
    public static bool TryRead(
        IConfiguration configuration,
        string sectionPath,
        out IReadOnlySet<Guid> values)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(sectionPath))
            throw new ArgumentException("Section path must not be empty.", nameof(sectionPath));

        if (configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers.Reverse())
            {
                var hasExactValue = provider.TryGet(sectionPath, out var exactValue);
                var childKeys = provider.GetChildKeys([], sectionPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (!hasExactValue && childKeys.Length == 0)
                    continue;

                return TryReadFromProvider(
                    provider, sectionPath, hasExactValue, exactValue, childKeys, out values);
            }

            values = new HashSet<Guid>();
            return true;
        }

        // Backward-compatible fallback for unusual IConfiguration wrappers that do not expose
        // their provider chain. The application and normal tests use IConfigurationRoot.
        return TryReadMergedSection(configuration.GetSection(sectionPath), out values);
    }

    /// <summary>
    /// Reads one list strictly from <paramref name="provider"/>. This is used by security
    /// configuration whose containing object has already selected an authoritative provider;
    /// falling back to a lower provider for one child would reintroduce split-snapshot scopes.
    /// A provider which does not declare the list represents an empty (deny-all) scope.
    /// </summary>
    internal static bool TryReadFromProvider(
        IConfigurationProvider provider,
        string sectionPath,
        out IReadOnlySet<Guid> values)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(sectionPath))
            throw new ArgumentException("Section path must not be empty.", nameof(sectionPath));

        var hasExactValue = provider.TryGet(sectionPath, out var exactValue);
        var childKeys = provider.GetChildKeys([], sectionPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!hasExactValue && childKeys.Length == 0)
        {
            values = new HashSet<Guid>();
            return true;
        }

        return TryReadFromProvider(
            provider, sectionPath, hasExactValue, exactValue, childKeys, out values);
    }

    private static bool TryReadFromProvider(
        IConfigurationProvider provider,
        string sectionPath,
        bool hasExactValue,
        string? exactValue,
        IReadOnlyCollection<string> childKeys,
        out IReadOnlySet<Guid> values)
    {
        // A provider declaring both an atomic scalar and indexed children is ambiguous. Never
        // guess which scope was intended; malformed authorization configuration is deny-all.
        if (hasExactValue && childKeys.Count > 0)
        {
            values = new HashSet<Guid>();
            return false;
        }

        if (hasExactValue)
        {
            // JsonConfigurationProvider uses an exact null entry for [] (and for an empty
            // object). Both must replace lower providers with an empty, fail-closed scope.
            if (string.IsNullOrWhiteSpace(exactValue))
            {
                values = new HashSet<Guid>();
                return true;
            }

            return TryReadJsonArray(exactValue, out values);
        }

        var parsed = new HashSet<Guid>();
        foreach (var childKey in childKeys)
        {
            if (!int.TryParse(childKey, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                || !provider.TryGet($"{sectionPath}:{childKey}", out var raw)
                || !Guid.TryParse(raw, out var id))
            {
                values = new HashSet<Guid>();
                return false;
            }

            parsed.Add(id);
        }

        values = parsed;
        return true;
    }

    private static bool TryReadJsonArray(string json, out IReadOnlySet<Guid> values)
    {
        var parsed = new HashSet<Guid>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                values = new HashSet<Guid>();
                return false;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(element.GetString(), out var id))
                {
                    values = new HashSet<Guid>();
                    return false;
                }

                parsed.Add(id);
            }
        }
        catch (JsonException)
        {
            values = new HashSet<Guid>();
            return false;
        }

        values = parsed;
        return true;
    }

    private static bool TryReadMergedSection(
        IConfigurationSection section,
        out IReadOnlySet<Guid> values)
    {
        var parsed = new HashSet<Guid>();
        foreach (var child in section.GetChildren())
        {
            if (!Guid.TryParse(child.Value, out var id))
            {
                values = new HashSet<Guid>();
                return false;
            }

            parsed.Add(id);
        }

        values = parsed;
        return true;
    }
}
