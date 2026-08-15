using NodePilot.Core.Models;

namespace NodePilot.Core.Security;

/// <summary>
/// Shared trust-boundary policy for generic SQL whose schema/results are sent to an external agent
/// (AI Knowledge or MCP). Browser DbAdmin is intentionally outside this policy: administrators may
/// inspect raw automation payloads there for forensics, while agent adapters expose those payloads
/// only through their dedicated, RBAC-aware tools.
/// </summary>
public static class ExternalAgentSqlPolicy
{
    public const string Mask = "***";

    /// <summary>
    /// Names the protected surface on purpose. The recipient is an LLM deciding what to try next:
    /// a generic "protected data" refusal invites it to rephrase the same query, while naming the
    /// workflow definition / custom activity implementation and pointing at the dedicated tool
    /// routes it somewhere that actually works.
    /// </summary>
    public const string RejectionMessage =
        "Query references a workflow definition or custom activity implementation. "
        + "Generic SQL cannot expose those to an external agent — "
        + "use the dedicated RBAC-aware API or tool for that data instead.";

    private static readonly ProtectedTable[] ProtectedTables =
    [
        CreateTable<Workflow>("Workflows", nameof(Workflow.DefinitionJson)),
        CreateTable<WorkflowVersion>("WorkflowVersions", nameof(WorkflowVersion.DefinitionJson)),
        CreateTable<CustomActivityDefinition>(
            "CustomActivityDefinitions",
            nameof(CustomActivityDefinition.ScriptTemplate),
            nameof(CustomActivityDefinition.InputParametersJson)),
        CreateTable<CustomActivityDefinitionVersion>(
            "CustomActivityDefinitionVersions",
            nameof(CustomActivityDefinitionVersion.ScriptTemplate),
            nameof(CustomActivityDefinitionVersion.InputParametersJson)),
    ];

    private static readonly HashSet<string> AllProtectedTableIdentifiers = ProtectedTables
        .SelectMany(table => table.Identifiers)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AllProtectedColumnIdentifiers = ProtectedTables
        .SelectMany(table => table.Columns)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Built-in opaque automation tables; callers may add metadata-derived secret tables.</summary>
    public static IReadOnlySet<string> BuiltInProtectedTableIdentifiers => AllProtectedTableIdentifiers;

    /// <summary>
    /// Generic agent SQL has no reliable provider-neutral way to prove projection lineage for these
    /// opaque rows. The complete tables are therefore absent from schema discovery; dedicated tools
    /// remain the only external-agent access path.
    /// </summary>
    public static bool IsSchemaTableVisible(
        string entityOrTableName,
        IReadOnlySet<string>? additionalProtectedTableIdentifiers = null)
        => !AllProtectedTableIdentifiers.Contains(entityOrTableName)
           && (additionalProtectedTableIdentifiers is null
               || !additionalProtectedTableIdentifiers.Contains(entityOrTableName));

    /// <summary>Whether a column may appear in generic external-agent schema discovery.</summary>
    public static bool IsSchemaColumnVisible(
        string entityOrTableName,
        string columnName,
        IReadOnlySet<string>? additionalProtectedTableIdentifiers = null)
        => IsSchemaTableVisible(entityOrTableName, additionalProtectedTableIdentifiers)
           && !AllProtectedColumnIdentifiers.Contains(columnName);

    /// <summary>
    /// Rejects every mention of a protected table, rather than attempting a provider-neutral SQL
    /// allow-list. Composite rows can flow through SELECT expressions, LATERAL sources, casts,
    /// aggregates and extension functions; lexical projection analysis cannot prove those safe.
    /// Protected column names and dynamic XML exporters are also rejected in case a view obscures
    /// the source table.
    /// </summary>
    public static bool ReferencesProtectedProjection(
        string sql,
        IReadOnlySet<string>? additionalProtectedTableIdentifiers = null)
    {
        return SqlStatementInspector.ContainsUnicodeEscapedIdentifier(sql)
               || SqlStatementInspector.FindDynamicDataExporter(sql) is not null
               || SqlStatementInspector.ReferencesAnyIdentifier(sql, AllProtectedTableIdentifiers)
               || (additionalProtectedTableIdentifiers is not null
                   && SqlStatementInspector.ReferencesAnyIdentifier(sql, additionalProtectedTableIdentifiers))
               || SqlStatementInspector.ReferencesAnyIdentifier(sql, AllProtectedColumnIdentifiers);
    }

    /// <summary>
    /// Result-name defence in depth for views/provider projections whose submitted SQL hides source
    /// lineage. A matching column is masked even when it belongs to an unrelated table; ambiguity at
    /// this point must resolve toward non-disclosure.
    /// </summary>
    public static bool IsProtectedResultColumn(string columnName)
        => AllProtectedColumnIdentifiers.Contains(columnName);

    private static ProtectedTable CreateTable<T>(string dbTableName, params string[] columns)
        => new(
            new HashSet<string>([typeof(T).Name, dbTableName], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase));

    private sealed record ProtectedTable(
        IReadOnlySet<string> Identifiers,
        IReadOnlySet<string> Columns);
}
