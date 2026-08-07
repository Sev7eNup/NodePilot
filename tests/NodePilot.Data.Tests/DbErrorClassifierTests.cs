using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using NodePilot.Data;
using System.Reflection;
using Xunit;

namespace NodePilot.Data.Tests;

/// <summary>
/// Covers <see cref="DbErrorClassifier.IsCommandTimeout"/>, which decides whether a failed request
/// is answered with "the database is busy, try again" (503) or with an anonymous 500. Getting the
/// classification wrong in either direction is bad: a false positive tells the user to retry a
/// request that will never succeed, a false negative hides a transient condition behind a generic
/// error.
/// </summary>
public sealed class DbErrorClassifierTests
{
    [Fact]
    public void IsCommandTimeout_PlainTimeoutException_IsTimeout()
    {
        // Npgsql surfaces a client-side command timeout as exactly this.
        DbErrorClassifier.IsCommandTimeout(new TimeoutException("Timeout during reading attempt"))
            .Should().BeTrue();
    }

    [Fact]
    public void IsCommandTimeout_TimeoutNestedInsideWrappers_IsTimeout()
    {
        // EF Core wraps provider exceptions, and its retrying execution strategy wraps them again
        // after the final attempt - so the interesting exception is rarely the outermost one.
        var nested = new InvalidOperationException(
            "An exception occurred while iterating over the results of a query",
            new AggregateException(new TimeoutException("Execution Timeout Expired")));

        DbErrorClassifier.IsCommandTimeout(nested).Should().BeTrue();
    }

    [Fact]
    public void IsCommandTimeout_PostgresQueryCanceled_IsTimeout()
    {
        // 57014 query_canceled is what statement_timeout produces server-side.
        DbErrorClassifier.IsCommandTimeout(new PostgresException(
            messageText: "canceling statement due to statement timeout",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: "57014")).Should().BeTrue();
    }

    [Fact]
    public void IsCommandTimeout_PostgresUniqueViolation_IsNotTimeout()
    {
        // A unique violation is permanent. Telling the caller to retry would be a lie.
        DbErrorClassifier.IsCommandTimeout(new PostgresException(
            messageText: "duplicate key value violates unique constraint",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: "23505")).Should().BeFalse();
    }

    [Fact]
    public void IsCommandTimeout_UnrelatedException_IsNotTimeout()
        => DbErrorClassifier.IsCommandTimeout(new InvalidOperationException("no such table"))
            .Should().BeFalse();

    [Fact]
    public void IsCommandTimeout_Null_IsNotTimeout()
        => DbErrorClassifier.IsCommandTimeout(null).Should().BeFalse();

    [Fact]
    public void IsCommandTimeout_DoesNotLoopForeverOnSelfReferencingException()
    {
        // Defensive: the walk follows InnerException, and an exception whose inner chain is deep
        // must terminate. Constructed as a long chain rather than a true cycle because the CLR
        // does not allow the latter.
        Exception chain = new TimeoutException();
        for (var i = 0; i < 200; i++) chain = new InvalidOperationException("wrapper", chain);

        DbErrorClassifier.IsCommandTimeout(chain).Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------------
    // Availability classification (DbFailureKind)
    // ---------------------------------------------------------------------------------------------

    // The next two tests are a PAIR and neither is meaningful alone: they feed the *same exception
    // object* to the two classification entry points and require opposite answers. That is the whole
    // "context beats shape" rule, expressed as an executable claim rather than a comment.
    //
    // Measured against Npgsql 10.0.3 on 2026-08-06 with a byte-swallowing TCP proxy in front of a real
    // PostgreSQL: a command timeout on a warm pooled connection arrives as
    //   NpgsqlException("Exception while reading from stream") -> TimeoutException
    // and a connect timeout against a hung-but-listening server arrives as
    //   NpgsqlException("The operation has timed out")         -> TimeoutException
    // Identical shape. No predicate reading only the exception can separate them.

    private static NpgsqlException NpgsqlTimeout() =>
        new("Exception while reading from stream", new TimeoutException("Timeout during reading attempt"));

    [Fact]
    public void Classify_NpgsqlExceptionWrappingTimeout_IsCommandTimeoutNotConnectionFailure()
    {
        // On the command path the honest answer is "one slow query" - the server answered the
        // handshake, it just did not finish this statement. Reading it as ConnectionFailure would let
        // a single locked table put a "database unavailable" banner in front of every user.
        DbErrorClassifier.Classify(NpgsqlTimeout()).Should().Be(DbFailureKind.CommandTimeout);
    }

    [Fact]
    public void ClassifyConnectionFailure_SameExceptionOnConnectHook_IsConnectionFailure()
    {
        // Same object, opposite answer: a physical open that timed out means no handshake completed,
        // which is a dead server rather than a slow query.
        DbErrorClassifier.ClassifyConnectionFailure(NpgsqlTimeout()).Should().Be(DbFailureKind.ConnectionFailure);
    }

    [Fact]
    public void ClassifyConnectionFailure_ServerAnsweredAndDeclined_StaysRejected()
    {
        // The two answers that must survive the fold: the server spoke. Reporting a wrong password as
        // an outage would hide a permanent misconfiguration behind a forever-"reconnecting" banner.
        var badPassword = new PostgresException(
            messageText: "password authentication failed for user \"nodepilot\"",
            severity: "FATAL", invariantSeverity: "FATAL", sqlState: "28P01");

        DbErrorClassifier.ClassifyConnectionFailure(badPassword).Should().Be(DbFailureKind.ConnectionRejected);
    }

    [Fact]
    public void Classify_NpgsqlPoolExhausted_IsCapacityBackpressure()
    {
        // Connection-failure-shaped, but proof the server is fine. Left in the fallthrough arm, one
        // burst of parallel steps against Max Pool Size=800 would take the whole installation down.
        var exhausted = new NpgsqlException(
            "The connection pool has been exhausted, either raise MaxPoolSize (currently 800) " +
            "or Timeout (currently 15 seconds)");

        DbErrorClassifier.Classify(exhausted).Should().Be(DbFailureKind.CapacityBackpressure);
        DbErrorClassifier.ClassifyConnectionFailure(exhausted).Should().Be(DbFailureKind.CapacityBackpressure);
    }

    [Fact]
    public void Classify_SqlClientPoolTimeout_IsCapacityBackpressure()
    {
        // SqlClient reports pool exhaustion as a plain InvalidOperationException - not a SqlException,
        // so it carries no Number and cannot be matched by the error-number table.
        var exhausted = new InvalidOperationException(
            "Timeout expired. The timeout period elapsed prior to obtaining a connection from the " +
            "pool. This may have occurred because all pooled connections were in use.");

        DbErrorClassifier.Classify(exhausted).Should().Be(DbFailureKind.CapacityBackpressure);
    }

    [Fact]
    public void Classify_CapacityBackpressureOutranksTimeoutInTheSameChain()
    {
        // Precedence is declaration order in DbFailureKind, and it must hold no matter which layer of
        // the chain each signal sits in.
        var chain = new InvalidOperationException(
            "An exception has been raised that is likely due to a transient failure.",
            new NpgsqlException("The connection pool has been exhausted",
                new TimeoutException("Timeout during reading attempt")));

        DbErrorClassifier.Classify(chain).Should().Be(DbFailureKind.CapacityBackpressure);
    }

    [Theory]
    [InlineData("53300", DbFailureKind.CapacityBackpressure)] // too_many_connections
    [InlineData("55P03", DbFailureKind.CapacityBackpressure)] // lock_not_available
    [InlineData("28P01", DbFailureKind.ConnectionRejected)]   // invalid_password
    [InlineData("28000", DbFailureKind.ConnectionRejected)]   // invalid_authorization (pg_hba)
    [InlineData("3D000", DbFailureKind.ConnectionRejected)]   // invalid_catalog_name
    [InlineData("57014", DbFailureKind.CommandTimeout)]       // query_canceled
    [InlineData("08006", DbFailureKind.ConnectionFailure)]    // connection_failure
    [InlineData("08001", DbFailureKind.ConnectionFailure)]    // sqlclient_unable_to_establish
    // The class-57 shutdown/startup states are deliberately ConnectionFailure, NOT rejections:
    // ConnectionRejected drives the probe's "configuration problem, retrying will not fix it" ERROR,
    // and 57P03 is what a server answers WHILE IT IS STARTING UP - a routine restart must read as
    // "unreachable, keep probing", or every restart scolds the operator about a config problem.
    [InlineData("57P01", DbFailureKind.ConnectionFailure)]    // admin_shutdown
    [InlineData("57P03", DbFailureKind.ConnectionFailure)]    // cannot_connect_now (crash recovery)
    [InlineData("57P05", DbFailureKind.None)]                 // idle_session_timeout - routine housekeeping
    [InlineData("40P01", DbFailureKind.None)]                 // deadlock_detected - the strategy's job
    [InlineData("23505", DbFailureKind.None)]                 // unique_violation
    [InlineData(null, DbFailureKind.None)]
    public void ClassifyBySqlState_Theory(string? sqlState, DbFailureKind expected)
        => DbErrorClassifier.ClassifyBySqlState(sqlState).Should().Be(expected);

    [Theory]
    [InlineData(1204, DbFailureKind.CapacityBackpressure)]  // out of locks
    [InlineData(1222, DbFailureKind.CapacityBackpressure)]  // lock request timeout; server is alive
    [InlineData(10928, DbFailureKind.CapacityBackpressure)] // Azure resource limit
    [InlineData(49918, DbFailureKind.CapacityBackpressure)] // cannot process request, not enough resources
    [InlineData(18456, DbFailureKind.ConnectionRejected)]   // login failed
    [InlineData(4060, DbFailureKind.ConnectionRejected)]    // cannot open database
    [InlineData(-2, DbFailureKind.CommandTimeout)]          // client gave up
    [InlineData(53, DbFailureKind.ConnectionFailure)]       // server not found
    [InlineData(10054, DbFailureKind.ConnectionFailure)]    // connection reset by peer
    [InlineData(233, DbFailureKind.ConnectionFailure)]      // no process on the other end of the pipe
    // Self-clearing server states - same reasoning as the Postgres class-57 rows above.
    [InlineData(945, DbFailureKind.ConnectionFailure)]      // database cannot be opened (recovering)
    [InlineData(18401, DbFailureKind.ConnectionFailure)]    // server is in script-upgrade mode
    [InlineData(40613, DbFailureKind.ConnectionFailure)]    // Azure database unavailable - documented transient
    [InlineData(1205, DbFailureKind.None)]                  // deadlock victim - the strategy's job
    [InlineData(2627, DbFailureKind.None)]                  // unique constraint
    public void ClassifyBySqlServerNumber_Theory(int number, DbFailureKind expected)
        => DbErrorClassifier.ClassifyBySqlServerNumber(number).Should().Be(expected);

    [Theory]
    [InlineData(18401, DbFailureKind.ConnectionFailure)]
    [InlineData(1222, DbFailureKind.CapacityBackpressure)]
    public void Classify_ProviderSqlException_UsesItsNativeNumber(
        int number,
        DbFailureKind expected)
        => DbErrorClassifier.Classify(CreateSqlException(number)).Should().Be(expected);

    [Fact]
    public void ClassifyConnectionFailure_UnknownOpenFailure_RemainsUnknownForProbeAdjudication()
        => DbErrorClassifier.ClassifyConnectionFailure(
                new InvalidOperationException("provider-specific open failure"))
            .Should().Be(DbFailureKind.None);

    [Fact]
    public void Classify_SqliteBusy_IsNone()
    {
        // SQLite is the in-memory test backend. A SQLITE_BUSY from a test fixture must never be able
        // to trip a production-shaped breaker, so the provider has no availability branch at all.
        DbErrorClassifier.Classify(new Microsoft.Data.Sqlite.SqliteException("database is locked", 5))
            .Should().Be(DbFailureKind.None);
    }

    [Fact]
    public void Classify_UnrelatedException_IsNone()
    {
        // The exception handlers require a non-None answer before they will speak for the database.
        // Without that, every NullReferenceException raised while the breaker happens to be open
        // would be reported to the client as "the database is down".
        DbErrorClassifier.Classify(new NullReferenceException()).Should().Be(DbFailureKind.None);
        DbErrorClassifier.Classify(null).Should().Be(DbFailureKind.None);
    }

    private static SqlException CreateSqlException(int number)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var errorConstructor = typeof(SqlError).GetConstructors(instance)
            .Where(candidate => candidate.GetParameters() is [{ ParameterType: var first }, ..]
                && first == typeof(int))
            .OrderBy(candidate => candidate.GetParameters().Length)
            .First();
        var error = (SqlError)errorConstructor.Invoke(BuildArguments(
            errorConstructor.GetParameters(), number));

        var collection = (SqlErrorCollection)Activator.CreateInstance(
            typeof(SqlErrorCollection), nonPublic: true)!;
        var add = typeof(SqlErrorCollection).GetMethods(instance)
            .Single(method => method.Name == "Add"
                && method.GetParameters() is [{ ParameterType: var parameterType }]
                && parameterType == typeof(SqlError));
        add.Invoke(collection, [error]);

        const BindingFlags factoryFlags = BindingFlags.Static | BindingFlags.NonPublic;
        var factory = typeof(SqlException).GetMethods(factoryFlags)
            .Where(method => method.Name == "CreateException"
                && method.ReturnType == typeof(SqlException)
                && method.GetParameters().FirstOrDefault()?.ParameterType == typeof(SqlErrorCollection))
            .OrderBy(method => method.GetParameters().Length)
            .First();
        return (SqlException)factory.Invoke(
            null,
            BuildArguments(factory.GetParameters(), collection))!;
    }

    private static object?[] BuildArguments(ParameterInfo[] parameters, object first)
    {
        var arguments = new object?[parameters.Length];
        arguments[0] = first;
        for (var index = 1; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            arguments[index] = parameter.ParameterType == typeof(string)
                ? "16.0.0"
                : parameter.HasDefaultValue
                    ? parameter.DefaultValue
                    : parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType)
                        : null;
        }
        return arguments;
    }
}
