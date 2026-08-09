using System.Text.Json;

namespace NodePilot.Core.Triggers;

/// <summary>
/// The parsed, validated config of a <c>databaseTrigger</c> node — the single vocabulary shared by
/// both runtimes that read it: <c>NodePilot.Engine.Triggers.DatabaseTrigger</c> (the node executor,
/// i.e. the manual sample run) and <c>NodePilot.Scheduler.Sources.DatabaseTriggerSource</c> (the
/// poll loop that actually fires the workflow).
///
/// <para>The two had drifted on the key that matters most: the designer, the documentation and the
/// node executor all speak <c>pollingIntervalSeconds</c>, while the poll loop read
/// <c>intervalSeconds</c> — so the interval an operator typed into the UI was dead and every
/// trigger polled at the source's own default. <c>provider</c> existed only on the poll loop and in
/// no documentation. Parsing, defaults, validation and connection resolution live here so both
/// paths cannot disagree again.</para>
///
/// <para>Firing semantics are the poll loop's: the query's first column of the first row is a
/// SENTINEL, and the workflow fires when it changes between polls. The node executor's row listing
/// is a diagnostic preview of that query, not a second firing rule.</para>
/// </summary>
public sealed class DatabaseTriggerSettings
{
    /// <summary>
    /// Kept at the value the poll loop has always actually used. The designer used to *display* 60
    /// for an absent key while the loop ran at 30 — the display was corrected to match, rather than
    /// the interval, so no existing trigger silently slows down.
    /// </summary>
    public const int DefaultPollingIntervalSeconds = 30;

    /// <summary>Floor on the poll interval — a tighter loop is a mistake, not a requirement.</summary>
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
    /// message when the config cannot produce a working trigger — the poll loop lets that surface as
    /// a registration failure (retried with backoff), the node executor turns it into a failed step.
    /// </summary>
    public static DatabaseTriggerSettings Parse(JsonElement config)
    {
        var query = ReadString(config, "query");
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("DatabaseTrigger: 'query' is required.");

        // H-1 (security audit 2026-05-15): a {{var}} template in the trigger query is always a
        // workflow-author mistake — the trigger runs *outside* a workflow run, so there is no
        // upstream step or manual parameter to substitute. Left alone it would either land
        // literally in CommandText or, if pre-fire resolution ever existed, become an injection
        // vector. Reject it where it is written, not where it explodes.
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

        // `intervalSeconds` was the poll loop's own spelling before both runtimes shared this type.
        // It stays a documented alias and keeps working: a hand-written or imported definition that
        // uses it must not lose its configured cadence. Exact key wins, same rule as
        // eventLogTrigger's entryType/level pair.
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
    /// <para>H-13: with <paramref name="requireRef"/> set (the default), an inline connection string
    /// is refused so workflow JSON cannot carry plaintext DB credentials into the process. Admins
    /// whitelist targets in appsettings and workflows reference them by name.</para>
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

    // Tolerant on purpose: the designer writes a JSON number, imported and AI-authored definitions
    // routinely carry the same value as a string, and a hard cast would fail registration for a
    // value the operator can see is fine.
    private static int? ReadInt32(JsonElement config, string key)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }
}
