using Microsoft.EntityFrameworkCore;

namespace NodePilot.Data;

/// <summary>
/// EF scaffolds each migration designer with the store types of whichever provider was
/// active at generation time. NodePilot intentionally uses one migration chain for SQL
/// Server, PostgreSQL, and SQLite tests, so those persisted type names must not override
/// the active provider's CLR type mapping when EF builds a migration target model.
/// </summary>
public static class MigrationModelPortability
{
    public static void UseActiveProviderStoreTypes(ModelBuilder modelBuilder)
    {
        foreach (var property in modelBuilder.Model.GetEntityTypes()
                     .SelectMany(entity => entity.GetProperties()))
        {
            property.SetColumnType(null);
        }
    }
}
