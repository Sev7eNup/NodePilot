using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Data;
using NodePilot.Engine;
using NodePilot.TestCommons;

namespace NodePilot.Engine.Tests.Helpers;

public static class TestDbContext
{
    public static NodePilotDbContext Create() => TestDbFactory.Create();

    /// <summary>
    /// Builds a test harness that shares a single in-memory SQLite connection between
    /// the engine's own <see cref="NodePilotDbContext"/> and the per-step DI scope.
    /// Returns the outer context plus a working <see cref="IServiceProvider"/> that can
    /// satisfy <c>CreateAsyncScope()</c> and resolve <see cref="NodePilotDbContext"/> +
    /// <see cref="ActivityRegistry"/> from scopes.
    /// </summary>
    public static (NodePilotDbContext db, ServiceProvider serviceProvider, SqliteConnection connection)
        CreateWithScopedServices(ActivityRegistry registry)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var serviceProvider = BuildScopeProviderOnSameConnection(connection, registry);

        var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        return (db, serviceProvider, connection);
    }

    /// <summary>
    /// Builds a standalone ServiceProvider bound to an existing SQLite connection. Useful
    /// when a single test wants to swap in a custom <see cref="ActivityRegistry"/> but
    /// still hit the same in-memory database as the outer test context.
    /// </summary>
    public static ServiceProvider BuildScopeProviderOnSameConnection(SqliteConnection connection, ActivityRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite(connection));
        services.AddScoped(_ => registry);
        return services.BuildServiceProvider();
    }
}
