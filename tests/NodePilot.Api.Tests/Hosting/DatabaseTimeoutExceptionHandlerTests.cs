using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Hosting;
using Npgsql;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// The handler that turns a database command timeout into 503 + DATABASE_TIMEOUT instead of an
/// anonymous 500.
///
/// <para>Exercised directly rather than through the HTTP pipeline on purpose:
/// <c>UseExceptionHandler()</c> is only registered outside Development (see
/// <c>SecurityPipelineSetup</c>), so a WebApplicationFactory test running as Development would hit
/// the developer exception page and never reach this handler at all - a test that passes while
/// proving nothing.</para>
/// </summary>
public sealed class DatabaseTimeoutExceptionHandlerTests
{
    private static DatabaseTimeoutExceptionHandler CreateHandler()
        => new(NullLogger<DatabaseTimeoutExceptionHandler>.Instance);

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        context.Request.Method = "GET";
        context.Request.Path = "/api/workflows";
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Handles_CommandTimeout_As503WithRetryAfterAndStableCode()
    {
        var context = CreateContext();

        var handled = await CreateHandler().TryHandleAsync(
            context, new TimeoutException("Execution Timeout Expired"), CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable,
            "a timeout is a transient load condition, not a bug in the request");
        context.Response.Headers["Retry-After"].ToString().Should().Be("5");

        using var document = JsonDocument.Parse(await ReadBodyAsync(context));
        document.RootElement.GetProperty("code").GetString()
            .Should().Be("DATABASE_TIMEOUT", "clients branch on the code, not on the prose");
        document.RootElement.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Handles_TimeoutWrappedByEntityFramework()
    {
        // What actually arrives: EF wraps the provider exception, and the retrying execution
        // strategy wraps it again. An outermost-only check would miss every real occurrence.
        var context = CreateContext();
        var wrapped = new InvalidOperationException(
            "An exception occurred while iterating over the results of a query",
            new TimeoutException("Execution Timeout Expired"));

        var handled = await CreateHandler().TryHandleAsync(context, wrapped, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Handles_PostgresStatementTimeout()
    {
        var context = CreateContext();
        var pgTimeout = new PostgresException(
            messageText: "canceling statement due to statement timeout",
            severity: "ERROR", invariantSeverity: "ERROR", sqlState: "57014");

        var handled = await CreateHandler().TryHandleAsync(context, pgTimeout, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Declines_UnrelatedException_AndLeavesTheResponseUntouched()
    {
        // Declining must be inert: writing a status here would hijack every other exception and
        // mask genuine 500s behind a misleading "try again".
        var context = CreateContext();
        var initialStatus = context.Response.StatusCode;

        var handled = await CreateHandler().TryHandleAsync(
            context, new InvalidOperationException("Sequence contains no elements"), CancellationToken.None);

        handled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(initialStatus);
        context.Response.Headers.Should().NotContainKey("Retry-After");
        (await ReadBodyAsync(context)).Should().BeEmpty();
    }

    [Fact]
    public async Task Declines_PermanentDatabaseError()
    {
        // A unique violation will fail identically on every retry; answering 503 + Retry-After
        // would send the caller into a pointless loop.
        var context = CreateContext();
        var permanent = new PostgresException(
            messageText: "duplicate key value violates unique constraint",
            severity: "ERROR", invariantSeverity: "ERROR", sqlState: "23505");

        (await CreateHandler().TryHandleAsync(context, permanent, CancellationToken.None))
            .Should().BeFalse();
    }
}
