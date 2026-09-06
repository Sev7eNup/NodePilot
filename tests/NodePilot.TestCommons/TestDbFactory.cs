using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodePilot.Data;

namespace NodePilot.TestCommons;

/// <summary>
/// Shared factory for in-memory SQLite contexts used across every test project.
/// Keeps the SQLite setup in one place so schema drift between Engine/Data/Api
/// tests is impossible.
/// </summary>
public static class TestDbFactory
{
    /// <summary>
    /// Creates a fresh in-memory SQLite DB and an <see cref="NodePilotDbContext"/>
    /// bound to it. The context owns the connection: disposing the context closes it.
    /// </summary>
    public static NodePilotDbContext Create() => Build(contextOwnsConnection: true).context;

    /// <summary>
    /// Opens a SQLite connection and returns it together with a fresh context. Use when
    /// the test needs to keep the connection alive across multiple contexts (e.g. to
    /// verify persistence between Dispose + re-open). The caller owns the connection.
    /// </summary>
    public static (SqliteConnection connection, NodePilotDbContext context) CreateWithConnection() =>
        Build(contextOwnsConnection: false);

    private static (SqliteConnection connection, NodePilotDbContext context) Build(bool contextOwnsConnection)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection, contextOwnsConnection)
            .Options;
        var context = new NodePilotDbContext(options);
        try
        {
            context.Database.EnsureCreated();
        }
        catch
        {
            context.Dispose();
            connection.Dispose();
            throw;
        }
        return (connection, context);
    }
}
