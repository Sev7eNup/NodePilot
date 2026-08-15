namespace NodePilot.Api.Services.DbAdmin;

/// <summary>
/// The single place that knows which App-DB columns must never leave the process in clear text.
/// Derived from <see cref="DbAdminPolicy"/> via <see cref="DbAdminMetadataService"/>, so the
/// row browser (<c>IsHidden</c>/<c>IsMasked</c>), the raw-SQL endpoint and the text2sql knowledge
/// reader all enforce the same set instead of each carrying its own copy.
///
/// <para>Two complementary layers, because neither alone is sufficient:</para>
/// <list type="number">
///   <item><b>Pre-execution rejection</b> (<see cref="ReferencesProtectedColumn"/>) — result-column
///   masking cannot recover lineage through an alias or expression
///   (<c>SELECT PasswordHash AS p</c>, <c>SELECT substr(PasswordHash,1,4)</c>), so any statement
///   that so much as names a hidden identifier is refused before it reaches the database.</item>
///   <item><b>Result masking</b> (<see cref="BuildColumnMask"/>) — a wildcard select
///   (<c>SELECT * FROM Users</c>) names no secret identifier but still returns one, so every
///   result column whose name matches a protected column is replaced with <c>"***"</c>.</item>
///   <item><b>Row-projection rejection</b> (<see cref="ReferencesProtectedRowProjection"/>) —
///   both layers above are NAME-based, and a row serializer defeats both at once:
///   <c>SELECT to_json(u) FROM "Users" u</c> never mentions <c>PasswordHash</c> (so layer 1 stays
///   quiet) and returns it inside a column called <c>to_json</c> (so layer 2 finds nothing to
///   mask). Statements that combine a protected table with a whole-row serializer are therefore
///   refused outright.</item>
/// </list>
///
/// <para>Registered as a singleton alongside <see cref="DbAdminMetadataService"/> — the EF model,
/// and therefore this set, is fixed for the process lifetime.</para>
/// </summary>
public sealed class DbAdminSecretColumns
{
    /// <summary>Replacement written into every protected result cell.</summary>
    public const string Mask = "***";

    /// <summary>
    /// GlobalVariable.Value is masked rather than hidden in <see cref="DbAdminPolicy"/> (operators
    /// need the row, not the secret). "Value" is a common, harmless column name elsewhere, so it is
    /// only *blocked* when the statement also names the GlobalVariable table.
    /// </summary>
    private static readonly HashSet<string> GlobalVariableTableIdentifiers =
        new(["GlobalVariable", "GlobalVariables"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> GlobalVariableValueIdentifier =
        new(["Value"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Result-column names that get masked: every hidden column plus GlobalVariable.Value.</summary>
    private readonly HashSet<string> _maskedColumnNames;

    /// <summary>Identifiers whose mere mention in a statement makes it unexecutable.</summary>
    private readonly HashSet<string> _blockedIdentifiers;

    /// <summary>
    /// Entity and DB-table names of every table that carries a masked column. Only these tables
    /// need the row-projection guard, which keeps the (necessarily blunt) rejection away from the
    /// ~34 tables that hold no secret at all.
    /// </summary>
    private readonly HashSet<string> _protectedTableIdentifiers;

    /// <summary>
    /// Entity and mapped SQL table identifiers whose rows contain a hidden/masked secret column.
    /// External-agent adapters deny these complete tables because provider-neutral SQL cannot prove
    /// that a composite row was not serialized under an alias.
    /// </summary>
    public IReadOnlySet<string> ProtectedTableIdentifiers => _protectedTableIdentifiers;

    public bool IsProtectedTableIdentifier(string identifier)
        => _protectedTableIdentifiers.Contains(identifier);

    public DbAdminSecretColumns(DbAdminMetadataService metadata)
    {
        _maskedColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _blockedIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _protectedTableIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in metadata.GetAllTables())
        {
            var tableHasSecret = false;
            foreach (var column in table.Columns)
            {
                if (column.IsHidden)
                {
                    _maskedColumnNames.Add(column.Name);
                    _blockedIdentifiers.Add(column.Name);
                    tableHasSecret = true;
                }
                else if (table.Name == "GlobalVariable" && column.Name == "Value")
                {
                    _maskedColumnNames.Add(column.Name);
                    tableHasSecret = true;
                }
            }

            if (!tableHasSecret) continue;
            // Both spellings: SQL addresses the DB table ("Users"), error messages and the
            // row browser use the entity name ("User"), and an LLM may reach for either.
            _protectedTableIdentifiers.Add(table.Name);
            _protectedTableIdentifiers.Add(table.DbTableName);
        }
    }

    /// <summary>
    /// True when <paramref name="sql"/> names a hidden column, or names both the GlobalVariable
    /// table and its Value column. Callers must refuse to execute such a statement.
    /// </summary>
    public bool ReferencesProtectedColumn(string sql)
        => DbAdminReadOnlySqlGuard.ReferencesAnyIdentifier(sql, _blockedIdentifiers)
           || (DbAdminReadOnlySqlGuard.ReferencesAnyIdentifier(sql, GlobalVariableTableIdentifiers)
               && DbAdminReadOnlySqlGuard.ReferencesAnyIdentifier(sql, GlobalVariableValueIdentifier));

    /// <summary>
    /// True when <paramref name="sql"/> serializes a whole row of a table that carries a masked
    /// column — <c>SELECT to_json(u) FROM "Users" u</c>, <c>SELECT u::text FROM "Users" u</c>,
    /// <c>SELECT * FROM Users FOR JSON AUTO</c>. Callers must refuse to execute such a statement:
    /// the projection carries the secret past both name-based layers.
    ///
    /// <para>Deliberately blunt — it fires on any combination of a protected table and a row
    /// serializer, including harmless ones such as <c>SELECT "Id"::text FROM "Users"</c>. Naming
    /// the wanted columns explicitly always works, and the error message says so. Being a
    /// blocklist it also cannot be exhaustive against every provider extension; the authoritative
    /// fix is a least-privilege DB login without SELECT on the secret columns (tracked in
    /// docs/security-findings.md).</para>
    /// </summary>
    public bool ReferencesProtectedRowProjection(string sql)
        => DbAdminReadOnlySqlGuard.ReferencesWholeRowProjection(sql, _protectedTableIdentifiers);

    /// <summary>
    /// Per-result-column flags: <c>true</c> where the cell must be replaced with <see cref="Mask"/>.
    /// </summary>
    public bool[] BuildColumnMask(IReadOnlyList<string> columnNames)
    {
        var mask = new bool[columnNames.Count];
        for (var i = 0; i < columnNames.Count; i++)
            mask[i] = _maskedColumnNames.Contains(columnNames[i]);
        return mask;
    }

    /// <summary>Applies <see cref="BuildColumnMask"/> in place over raw executor rows.</summary>
    public void MaskRows(IReadOnlyList<string> columnNames, List<List<object?>> rows)
    {
        var mask = BuildColumnMask(columnNames);
        if (Array.TrueForAll(mask, m => !m)) return;

        foreach (var row in rows)
        {
            for (var c = 0; c < row.Count && c < mask.Length; c++)
                if (mask[c]) row[c] = Mask;
        }
    }
}
