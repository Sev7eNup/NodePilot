using FluentAssertions;
using NodePilot.Data.Availability;
using Xunit;

namespace NodePilot.Data.Tests.Availability;

/// <summary>
/// The connection-string surgery is small, but two of its facts were established the hard way and
/// must not regress silently:
///
/// 1. Npgsql's connect-timeout keyword is <c>Timeout</c> — <c>Connect Timeout</c> does not exist
///    there (verified against the Npgsql 10.0.3 keyword table). Writing SQL Server's spelling into
///    a PostgreSQL string throws at runtime.
/// 2. The connect timeout sits on the critical path TWICE for every command timeout against a
///    wedged server (measured: elapsed = CommandTimeout + 2 x ConnectTimeout, because the driver
///    sends its cancel request over a fresh connection into the same wedge).
/// </summary>
public sealed class DatabaseConnectionStringTests
{
    [Fact]
    public void EnsureConnectTimeout_Postgres_UsesTheTimeoutKeyword()
    {
        var result = DatabaseConnectionString.EnsureConnectTimeout(
            "postgres", "Host=db;Database=nodepilot;Username=np", 5);

        result.Should().Contain("Timeout=5");
        result.Should().NotContain("Connect Timeout");
    }

    [Fact]
    public void EnsureConnectTimeout_SqlServer_UsesConnectTimeout()
    {
        var result = DatabaseConnectionString.EnsureConnectTimeout(
            "sqlserver", "Server=sql01;Database=NodePilot;Trusted_Connection=True", 5);

        result.Should().Contain("Connect Timeout=5");
    }

    [Fact]
    public void EnsureConnectTimeout_OperatorValueWins()
    {
        // An explicit choice in the connection string is configuration, not an omission.
        var result = DatabaseConnectionString.EnsureConnectTimeout(
            "postgres", "Host=db;Database=nodepilot;Timeout=30", 5);

        result.Should().Contain("Timeout=30");
        result.Should().NotContain("Timeout=5");
    }

    [Fact]
    public void EnsureConnectTimeout_SqlServerSynonym_IsRespected()
    {
        // "Connection Timeout" is SqlClient's documented synonym; stamping the canonical key on
        // top would produce two competing values.
        var result = DatabaseConnectionString.EnsureConnectTimeout(
            "sqlserver", "Server=sql01;Database=NodePilot;Connection Timeout=30", 5);

        result.Should().NotContain("Connect Timeout=5");
    }

    [Fact]
    public void EnsureConnectTimeout_UnparseableString_PassesThroughUnchanged()
    {
        // The provider raises a far better error moments later — and this method must never log
        // or throw with the string, which carries the password. Input chosen to actually make
        // DbConnectionStringBuilder throw: a value without a key. (Plain word salad does NOT
        // throw — the builder parses it leniently — so it would not exercise the catch.)
        const string garbage = "=value-without-a-key";
        DatabaseConnectionString.EnsureConnectTimeout("postgres", garbage, 5).Should().Be(garbage);
    }

    [Fact]
    public void ForProbe_Postgres_DisablesPoolingAndNamesItself()
    {
        var result = DatabaseConnectionString.ForProbe(
            "postgres", "Host=db;Database=nodepilot;Username=np;Maximum Pool Size=800", 2, 2);

        // Pooling off: under pool exhaustion the probe must not queue behind the very callers it
        // is adjudicating. The application name makes it identifiable in pg_stat_activity.
        result.Should().Contain("Pooling=False");
        result.Should().Contain("NodePilot-Probe");
        result.Should().Contain("Timeout=2");
    }

    [Fact]
    public void ForProbe_SqlServer_DisablesPoolingAndNamesItself()
    {
        var result = DatabaseConnectionString.ForProbe(
            "sqlserver", "Server=sql01;Database=NodePilot;Trusted_Connection=True", 2, 2);

        result.Should().Contain("Pooling=False");
        result.Should().Contain("NodePilot-Probe");
        result.Should().Contain("Connect Timeout=2");
    }

    [Fact]
    public void ForProbe_ZeroTimeouts_AreClampedToOne()
    {
        // CommandTimeout = 0 means INFINITE in ADO.NET; on the probe that would wedge the only
        // path back to Available, forever.
        var result = DatabaseConnectionString.ForProbe("postgres", "Host=db;Database=np", 0, 0);

        result.Should().Contain("Timeout=1");
        result.Should().Contain("Command Timeout=1");
    }
}
