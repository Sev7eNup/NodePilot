using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NodePilot.Data;

namespace NodePilot.Api.Services.DbAdmin;

/// <summary>
/// Singleton that reflects on the EF Core model once at startup to build schema metadata
/// for all tracked entity types. Schema never changes at runtime, so it is safe to cache.
/// Uses IServiceScopeFactory to resolve the scoped DbContext during construction only.
/// </summary>
public sealed class DbAdminMetadataService
{
    private readonly IReadOnlyDictionary<string, TableMeta> _tables;

    public DbAdminMetadataService(IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();

        var map = new Dictionary<string, TableMeta>(StringComparer.OrdinalIgnoreCase);

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            // Skip owned types and shadow-only types that have no CLR class
            if (entityType.ClrType is null) continue;
            // Skip junction tables owned/keyless entities that we'd rather not expose
            if (entityType.IsOwned()) continue;

            var name = entityType.ClrType.Name;
            // The real DB-side table name (pluralised in our migrations: "Credentials"
            // not "Credential"). Needed by the SQL query console so click-to-insert
            // produces a name that Postgres can actually resolve. Falls back to the
            // CLR name when GetTableName returns null (shadow/owned types we already
            // skip above, but be defensive).
            var dbTableName = entityType.GetTableName() ?? name;
            var pkProps = entityType.FindPrimaryKey()?.Properties ?? Array.Empty<IProperty>().AsEnumerable();
            var pkNames = pkProps.Select(p => p.Name).ToList();

            // Cascade delete targets: find entities whose FK to this entity has DeleteBehavior.Cascade
            var cascadeTo = db.Model.GetEntityTypes()
                .SelectMany(e => e.GetForeignKeys())
                .Where(fk => fk.PrincipalEntityType == entityType && fk.DeleteBehavior == DeleteBehavior.Cascade)
                .Select(fk => fk.DeclaringEntityType.ClrType.Name)
                .Distinct()
                .ToList();

            var caps = DbAdminPolicy.GetCapabilities(name);

            var columns = new List<ColumnMeta>();
            foreach (var prop in entityType.GetProperties())
            {
                var clrType = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
                var isNullable = prop.IsNullable ||
                                 (prop.ClrType.IsGenericType && prop.ClrType.GetGenericTypeDefinition() == typeof(Nullable<>));
                var isPk = pkNames.Contains(prop.Name, StringComparer.Ordinal);
                var isHidden = DbAdminPolicy.IsColumnHidden(name, prop.Name, clrType);
                var isReadOnly = DbAdminPolicy.IsColumnEffectivelyReadOnly(name, prop.Name, isPk, clrType);
                int? maxLen = prop.GetMaxLength();

                columns.Add(new ColumnMeta(
                    Name: prop.Name,
                    ClrType: clrType,
                    IsNullable: isNullable,
                    MaxLength: maxLen,
                    IsPrimaryKey: isPk,
                    IsHidden: isHidden,
                    IsReadOnly: isReadOnly
                ));
            }

            map[name] = new TableMeta(
                EntityType: entityType,
                Name: name,
                DbTableName: dbTableName,
                PkColumns: pkNames,
                Capabilities: caps,
                Columns: columns,
                CascadeDeletesTo: cascadeTo
            );
        }

        _tables = map;
    }

    public IEnumerable<string> TableNames => _tables.Keys;

    public TableMeta? GetTable(string name)
        => _tables.TryGetValue(name, out var t) ? t : null;

    public IEnumerable<TableMeta> GetAllTables() => _tables.Values;
}

public record TableMeta(
    IEntityType EntityType,
    string Name,
    string DbTableName,
    List<string> PkColumns,
    DbAdminPolicy.EntityCapabilities Capabilities,
    List<ColumnMeta> Columns,
    List<string> CascadeDeletesTo
);

public record ColumnMeta(
    string Name,
    Type ClrType,
    bool IsNullable,
    int? MaxLength,
    bool IsPrimaryKey,
    bool IsHidden,
    bool IsReadOnly
);
