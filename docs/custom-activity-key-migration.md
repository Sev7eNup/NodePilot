# Duplicate custom-activity keys before migration

Migration `20261009112618_EnforceLiveCustomActivityKeys` adds a unique index over
non-deleted custom-activity keys. Drafts and enabled definitions share the key
namespace. Deleted definitions may retain their keys and history.

If existing live definitions have the same key, startup stops with a diagnostic.
The migration does not delete, rename or merge definitions. Resolve the conflict
explicitly before retrying startup; references use the definition ID as well as
the key, so an automatic rename or deletion could break workflows. Take a backup
and inspect the affected workflow references before choosing a surviving definition.

These queries only list conflicts. Run the version for your database:

PostgreSQL:

```sql
SELECT "Key", COUNT(*) AS "Count"
FROM "CustomActivityDefinitions"
WHERE "IsDeleted" = FALSE
GROUP BY "Key"
HAVING COUNT(*) > 1;
```

SQL Server:

```sql
SELECT [Key], COUNT(*) AS [Count]
FROM [CustomActivityDefinitions]
WHERE [IsDeleted] = 0
GROUP BY [Key]
HAVING COUNT(*) > 1;
```

SQLite test/local databases use the PostgreSQL query with `0` in place of `FALSE`.
Key comparison follows the database's existing key-column collation; the migration
does not change case sensitivity.
