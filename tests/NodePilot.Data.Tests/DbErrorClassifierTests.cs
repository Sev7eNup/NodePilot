using FluentAssertions;
using Npgsql;
using NodePilot.Data;
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

    // NOTE on the SQL Server branch: Microsoft.Data.SqlClient.SqlException has no public
    // constructor, so error -2 cannot be exercised from a test without reflection into internal
    // provider types that changes between versions. The branch mirrors the already-shipping
    // IsUniqueConstraintViolation reflection lookup on the same Number property, and is covered in
    // the field rather than here - the timeout that motivated this classifier was a real
    // SqlException with Number = -2 on the lab host. Stated rather than silently omitted.
}
