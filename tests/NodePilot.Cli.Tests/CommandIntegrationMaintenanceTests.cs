using FluentAssertions;
using NodePilot.Cli;
using NodePilot.Cli.Tests.Infra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// End-to-end command tests for <c>np maintenance ...</c>. These exercise real Spectre command
/// parsing, exit codes, and stdout/stderr shape — not the HTTP client (that's
/// <see cref="NodePilotApiClientMaintenanceWindowsTests"/>). They cover the option-to-request
/// translation (days/time parsing), the get/update read-modify path, and the non-interactive
/// delete.
/// </summary>
[Collection(CommandTestCollection.Name)]
public class CommandIntegrationMaintenanceTests
{
    private static object WindowJson(Guid id, string name, string scope = "Global") => new
    {
        id,
        name,
        description = (string?)null,
        isEnabled = true,
        mode = "Blackout",
        scopeKind = scope,
        recurrence = "Weekly",
        oneTimeStartUtc = (DateTime?)null,
        oneTimeEndUtc = (DateTime?)null,
        weeklyDaysMask = 0b0111110,
        weeklyStartMinuteOfDay = 22 * 60,
        weeklyEndMinuteOfDay = 23 * 60,
        cronExpression = (string?)null,
        durationMinutes = (int?)null,
        timeZoneId = "UTC",
        targets = Array.Empty<object>(),
        createdAt = DateTime.UtcNow,
        updatedAt = DateTime.UtcNow,
        updatedBy = "admin",
    };

    [Fact]
    public void MaintenanceList_RendersJson()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/maintenance-windows").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                WindowJson(Guid.NewGuid(), "Nightly"),
            }));

        var result = h.Run("maintenance", "list");
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Nightly");
    }

    [Fact]
    public void MaintenanceGet_FoundById_RendersWindow()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        // `get` filters the list response client-side, so the list endpoint is what gets hit.
        h.Server.Given(Request.Create().WithPath("/api/maintenance-windows").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                WindowJson(id, "Nightly"),
                WindowJson(Guid.NewGuid(), "Other"),
            }));

        var result = h.Run("maintenance", "get", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
        result.Output.Should().Contain("Nightly");
    }

    [Fact]
    public void MaintenanceGet_NotFound_ReturnsError()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/maintenance-windows").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        var result = h.Run("maintenance", "get", Guid.NewGuid().ToString());
        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("nicht gefunden");
    }

    [Fact]
    public void MaintenanceCreate_TranslatesDaysAndTime_AndPostsBody()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/maintenance-windows").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(WindowJson(Guid.NewGuid(), "Nightly")));

        var result = h.Run("maintenance", "create",
            "--name", "Nightly", "--days", "Mon,Tue,Wed,Thu,Fri", "--start", "22:00", "--end", "23:30");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.StdErr.Should().Contain("Nightly");

        // Mon-Fri == bits 1..5 == 0b0111110 == 62; 22:00 == 1320; 23:30 == 1410.
        var body = h.Server.LogEntries.Last().RequestMessage.Body!;
        body.Should().Contain("\"weeklyDaysMask\":62");
        body.Should().Contain("\"weeklyStartMinuteOfDay\":1320");
        body.Should().Contain("\"weeklyEndMinuteOfDay\":1410");
    }

    [Fact]
    public void MaintenanceCreate_WithCronRecurrence_PostsCronBody()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/maintenance-windows").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(WindowJson(Guid.NewGuid(), "SatPatch")));

        var result = h.Run("maintenance", "create",
            "--name", "SatPatch", "--recurrence", "Cron",
            "--cron", "0 0 3 ? * SAT", "--duration-minutes", "90");

        result.ExitCode.Should().Be(ExitCodes.Success);

        var body = h.Server.LogEntries.Last().RequestMessage.Body!;
        body.Should().Contain("\"recurrence\":\"Cron\"");
        body.Should().Contain("\"cronExpression\":\"0 0 3 ? * SAT\"");
        body.Should().Contain("\"durationMinutes\":90");
    }

    [Fact]
    public void MaintenanceUpdate_CronFlags_OverrideCurrentValues()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        h.Server.Given(Request.Create().WithPath("/api/maintenance-windows").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                WindowJson(id, "Nightly"),
            }));
        h.Server.Given(Request.Create().WithPath($"/api/maintenance-windows/{id}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("maintenance", "update", id.ToString(),
            "--recurrence", "Cron", "--cron", "0 30 1 * * ?", "--duration-minutes", "45");

        result.ExitCode.Should().Be(ExitCodes.Success);
        var put = h.Server.LogEntries.Last(e => e.RequestMessage.Method == "PUT");
        put.RequestMessage.Body.Should().Contain("\"recurrence\":\"Cron\"");
        put.RequestMessage.Body.Should().Contain("\"cronExpression\":\"0 30 1 * * ?\"");
        put.RequestMessage.Body.Should().Contain("\"durationMinutes\":45");
    }

    [Fact]
    public void MaintenanceCreate_MissingName_FailsBeforeFiring()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("maintenance", "create", "--days", "Mon", "--start", "22:00", "--end", "23:00");

        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("--name");
        h.Server.LogEntries.Should().NotContain(e =>
            e.RequestMessage.AbsolutePath == "/api/maintenance-windows"
            && e.RequestMessage.Method == "POST");
    }

    [Fact]
    public void MaintenanceCreate_InvalidTime_FailsBeforeFiring()
    {
        using var h = new CommandTestHarness();
        var result = h.Run("maintenance", "create", "--name", "Bad", "--days", "Mon", "--start", "99:99");

        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("Ungültige Zeit");
        h.Server.LogEntries.Should().NotContain(e =>
            e.RequestMessage.AbsolutePath == "/api/maintenance-windows"
            && e.RequestMessage.Method == "POST");
    }

    [Fact]
    public void MaintenanceUpdate_FetchesCurrentThenPuts()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        // update reads the current window via the list endpoint, then PUTs the merged request.
        h.Server.Given(Request.Create().WithPath("/api/maintenance-windows").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                WindowJson(id, "Nightly"),
            }));
        h.Server.Given(Request.Create().WithPath($"/api/maintenance-windows/{id}").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("maintenance", "update", id.ToString(), "--name", "Renamed", "--mode", "AllowOnly");

        result.ExitCode.Should().Be(ExitCodes.Success);
        result.StdErr.Should().Contain("Renamed");

        // Only --name and --mode were passed; everything else must be carried over from current.
        var put = h.Server.LogEntries.Last(e => e.RequestMessage.Method == "PUT");
        put.RequestMessage.Body.Should().Contain("Renamed").And.Contain("AllowOnly");
        put.RequestMessage.Body.Should().Contain("\"timeZoneId\":\"UTC\"");
    }

    [Fact]
    public void MaintenanceUpdate_UnknownId_ReturnsError()
    {
        using var h = new CommandTestHarness();
        h.Server.Given(Request.Create().WithPath("/api/maintenance-windows").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

        var result = h.Run("maintenance", "update", Guid.NewGuid().ToString(), "--name", "Renamed");

        result.ExitCode.Should().Be(ExitCodes.Error);
        result.StdErr.Should().Contain("nicht gefunden");
    }

    [Fact]
    public void MaintenanceDelete_NonInteractive_DeletesWithoutPrompt()
    {
        using var h = new CommandTestHarness();
        var id = Guid.NewGuid();
        // CommandAppTester runs without an attached console — Console.IsInputRedirected is
        // true, which the delete command uses to skip the interactive confirm.
        h.Server.Given(Request.Create().WithPath($"/api/maintenance-windows/{id}").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = h.Run("maintenance", "delete", id.ToString());
        result.ExitCode.Should().Be(ExitCodes.Success);
        h.Server.LogEntries.Should().Contain(e =>
            e.RequestMessage.AbsolutePath == $"/api/maintenance-windows/{id}"
            && e.RequestMessage.Method == "DELETE");
    }
}
