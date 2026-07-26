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

    public DbAdminSecretColumns(DbAdminMetadataService metadata)
    {
        _maskedColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _blockedIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in metadata.GetAllTables())
        {
            foreach (var column in table.Columns)
            {
                if (column.IsHidden)
                {
                    _maskedColumnNames.Add(column.Name);
                    _blockedIdentifiers.Add(column.Name);
                }
                else if (table.Name == "GlobalVariable" && column.Name == "Value")
                {
                    _maskedColumnNames.Add(column.Name);
                }
            }
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
