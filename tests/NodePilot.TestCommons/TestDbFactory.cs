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
    /// bound to it. The caller owns the context — Dispose it to close the connection.
    /// </summary>
    public static NodePilotDbContext Create() => Build().context;

    /// <summary>
    /// Opens a SQLite connection and returns it together with a fresh context. Use when
    /// the test needs to keep the connection alive across multiple contexts (e.g. to
    /// verify persistence between Dispose + re-open).
    /// </summary>
    public static (SqliteConnection connection, NodePilotDbContext context) CreateWithConnection() => Build();

    private static (SqliteConnection connection, NodePilotDbContext context) Build()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection)
            .Options;
        var context = new NodePilotDbContext(options);
        context.Database.EnsureCreated();
        return (connection, context);
    }
}
