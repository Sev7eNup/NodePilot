using System.Text.Json;

namespace NodePilot.Core.Triggers;

/// <summary>
/// Parsed and validated config of a <c>databaseTrigger</c> node, shared by the two runtimes that
/// read it: <c>NodePilot.Engine.Triggers.DatabaseTrigger</c> (the node executor behind a manual
/// sample run) and <c>NodePilot.Scheduler.Sources.DatabaseTriggerSource</c> (the poll loop that
/// fires the workflow). Keys, defaults, validation and connection resolution live here so the two
/// paths cannot disagree.
///
/// <para>The poll loop defines the firing semantics: the first column of the query's first row is
/// a sentinel, and the workflow fires when that value changes between polls. The node executor's
/// row listing is a diagnostic preview of the query, not a second firing rule.</para>
/// </summary>
public sealed class DatabaseTriggerSettings
{
    /// <summary>
    /// Poll interval applied when the node config does not set one. The designer shows the same
    /// value for an absent key.
    /// </summary>
    public const int DefaultPollingIntervalSeconds = 30;

    /// <summary>Lower bound on the poll interval; a tighter loop is treated as a mistake.</summary>
    public const int MinPollingIntervalSeconds = 5;

    public const string DefaultProvider = "sqlserver";

    public static readonly IReadOnlyList<string> SupportedProviders = ["sqlserver", "sqlite"];

    public required string Query { get; init; }
    public string? ConnectionRef { get; init; }
    public string? InlineConnectionString { get; init; }
    public required string Provider { get; init; }
    public int PollingIntervalSeconds { get; init; }

    /// <summary>
    /// Parses a node config. Throws <see cref="InvalidOperationException"/> with an operator-facing
    /// message when the config cannot produce a working trigger. The poll loop surfaces that as a
    /// registration failure (retried with backoff); the node executor turns it into a failed step.
    /// </summary>
    public static DatabaseTriggerSettings Parse(JsonElement config)
    {
        var query = ReadString(config, "query");
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("DatabaseTrigger: 'query' is required.");

        // A {{var}} template in the trigger query is always a mistake: the trigger runs outside a
        // workflow run, so there is no upstream step or manual parameter to substitute. Rejecting
        // it here keeps it out of CommandText, where it would be dead text or an injection vector.
        if (query.Contains("{{", StringComparison.Ordinal) && query.Contains("}}", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "DatabaseTrigger: 'query' must not contain {{...}} templates. Trigger queries run before any "
                + "workflow step exists and have no variable context. Embed literal SQL only — pass dynamic "
                + "values through the workflow definition instead.");

        var provider = ReadString(config, "provider")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(provider)) provider = DefaultProvider;
        if (!SupportedProviders.Contains(provider))
            throw new InvalidOperationException(
                $"DatabaseTrigger: provider '{provider}' is not supported. Use one of: "
                + $"{string.Join(", ", SupportedProviders)}.");

        // `intervalSeconds` is a documented alias, so hand-written and imported definitions keep
        // their configured cadence. The exact key wins, same rule as eventLogTrigger's
        // entryType/level pair.
        var interval = ReadInt32(config, "pollingIntervalSeconds")
                       ?? ReadInt32(config, "intervalSeconds")
                       ?? DefaultPollingIntervalSeconds;

        return new DatabaseTriggerSettings
        {
            Query = query!,
            ConnectionRef = NullIfBlank(ReadString(config, "connectionRef")),
            InlineConnectionString = NullIfBlank(ReadString(config, "connectionString")),
            Provider = provider,
            PollingIntervalSeconds = Math.Max(MinPollingIntervalSeconds, interval),
        };
    }

    /// <summary>
    /// Resolves the connection string. <paramref name="lookupNamed"/> reads
    /// <c>Trigger:Database:Connections:{name}</c>; <paramref name="requireRef"/> is
    /// <c>Trigger:Database:RequireConnectionRef</c>.
    ///
    /// <para>With <paramref name="requireRef"/> set (the default), an inline connection string is
    /// refused so workflow JSON cannot carry plaintext DB credentials. Admins register targets in
    /// appsettings and workflows reference them by name.</para>
    /// </summary>
    public string ResolveConnectionString(Func<string, string?> lookupNamed, bool requireRef)
    {
        ArgumentNullException.ThrowIfNull(lookupNamed);

        if (!string.IsNullOrWhiteSpace(ConnectionRef))
        {
            var fromConfig = lookupNamed(ConnectionRef);
            if (string.IsNullOrWhiteSpace(fromConfig))
                throw new InvalidOperationException(
                    $"DatabaseTrigger: connectionRef '{ConnectionRef}' is not defined under "
                    + "Trigger:Database:Connections.");
            return fromConfig;
        }

        if (!string.IsNullOrWhiteSpace(InlineConnectionString))
        {
            if (requireRef)
                throw new InvalidOperationException(
                    "DatabaseTrigger: inline connectionString is disabled "
                    + "(Trigger:Database:RequireConnectionRef=true). Use connectionRef with a name "
                    + "registered under Trigger:Database:Connections.");
            return InlineConnectionString;
        }

        throw new InvalidOperationException(
            "DatabaseTrigger: either 'connectionRef' (preferred) or 'connectionString' is required.");
    }

    private static string? ReadString(JsonElement config, string key) =>
        config.ValueKind == JsonValueKind.Object && config.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // Accepts both shapes on purpose: the designer writes a JSON number, while imported and
    // AI-authored definitions often carry the same value as a string.
    private static int? ReadInt32(JsonElement config, string key)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }
}
