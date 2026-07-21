using FluentAssertions;
using NodePilot.Cli;
using NodePilot.Cli.Tests.Infra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// Table-rendering coverage for the operations / observability / db-query commands. These
/// verbs default to JSON in scripts, but the human-facing <c>-o table</c> render path holds
/// real logic (edge target resolution, Prometheus vector shaping, cell type formatting) that
/// JSON serialization bypasses — so each test forces <c>-o table</c> and asserts the rendered
/// content.
/// </summary>
[Collection(CommandTestCollection.Name)]
public class OpsObservabilityDbCommandTests
{
    // ---- operations graph ---------------------------------------------------

    [Fact]
    public void OperationsGraph_Table_RendersNodesAndEdges()
    {
        using var h = new CommandTestHarness();
        var wfA = Guid.NewGuid();
        var wfB = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath("/api/operations/graph").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                nodes = new object[]
                {
                    new { workflowId = wfA, name = "Deploy App", folderId = Guid.NewGuid(), folderPath = "/ops",
                          isEnabled = true, runningCount = 2, lastStatus = "Running", callFrequency = (int?)null },
                    new { workflowId = wfB, name = "Cleanup", folderId = Guid.NewGuid(), folderPath = "/ops",
                          isEnabled = false, runningCount = 0, lastStatus = (string?)null, callFrequency = (int?)null },
                },
                edges = new object[]
                {
                    // resolvable target → renders the callee's name
                    new { id = "e1", source = wfA, target = wfB, kind = "startWorkflow", refStatus = "resolved", rawRef = "Cleanup", callCount = 3 },
                    // unresolved target → renders "{refStatus}: {rawRef}"
                    new { id = "e2", source = wfA, target = (Guid?)null, kind = "forEach", refStatus = "missing", rawRef = "Ghost", callCount = 0 },
                },
                running = Array.Empty<object>(),
                recent = Array.Empty<object>(),
                capabilities = new { canCancel = true },
            }));

        var result = h.Run("operations", "graph", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Deploy App").And.Contain("Cleanup");
        result.Output.Should().Contain("Workflows:");
        result.Output.Should().Contain("missing: Ghost");
    }

    // ---- observability query ------------------------------------------------

    [Fact]
    public void ObservabilityQuery_VectorResult_RendersTable()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/observability/query").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                status = "success",
                data = new
                {
                    resultType = "vector",
                    result = new object[]
                    {
                        new { metric = new { job = "api", instance = "host-1" }, value = new object[] { 1_700_000_000, "42.5" } },
                    },
                },
            }));

        var result = h.Run("observability", "query", "--query", "up", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("42.5");
        result.Output.Should().Contain("job=\"api\"");
    }

    [Fact]
    public void ObservabilityQuery_NonEnvelopeBody_FallsBackToJson()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/observability/query").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                status = "error", errorType = "bad_data", error = "unknown metric",
            }));

        var result = h.Run("observability", "query", "--query", "nope", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("bad_data");
    }

    [Fact]
    public void ObservabilityQuery_MissingQuery_FailsWithError()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("observability", "query", "-o", "table");
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("--query");
    }

    // ---- db query -----------------------------------------------------------

    [Fact]
    public void DbQuery_Table_RendersColumnsAndFormatsEveryCellKind()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/dbadmin/query").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                columns = new object[]
                {
                    new { name = "s", type = "text" },
                    new { name = "n", type = "int" },
                    new { name = "b1", type = "bool" },
                    new { name = "b2", type = "bool" },
                    new { name = "j", type = "json" },
                },
                rows = new object[]
                {
                    new object?[] { "hello", 7, true, false, null },
                    new object?[] { "", 0, false, true, new { nested = "value" } },
                },
                rowsAffected = (int?)null,
                durationMs = 12,
                truncated = true,
                mode = "read",
            }));

        var result = h.Run("db", "query", "--sql", "SELECT * FROM t", "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("hello");
        result.Output.Should().Contain("NULL");     // JsonValueKind.Null cell
        result.Output.Should().Contain("true").And.Contain("false");
        result.Output.Should().Contain("truncated"); // Truncated flag surfaced
    }

    [Fact]
    public void DbQuery_SqlFromFile_IsReadAndExecuted()
    {
        using var h = new CommandTestHarness();
        var sqlFile = Path.Combine(h.ConfigDir, "query.sql");
        File.WriteAllText(sqlFile, "SELECT 1");
        h.Server.Given(Request.Create().WithPath("/api/dbadmin/query").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                columns = Array.Empty<object>(),
                rows = Array.Empty<object>(),
                rowsAffected = (int?)null,
                durationMs = 3,
                truncated = false,
                mode = "read",
            }));

        var result = h.Run("db", "query", "--file", sqlFile, "-o", "table");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("no result set");
        h.Server.LogEntries.Should().Contain(e => e.RequestMessage.AbsolutePath == "/api/dbadmin/query");
    }

    [Fact]
    public void DbQuery_NoSqlOrFile_FailsWithError()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("db", "query", "-o", "table");
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("--sql");
    }
}
