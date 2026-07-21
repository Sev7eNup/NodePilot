using FluentAssertions;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// API-client tests for the maintenance-windows surface (<c>np maintenance ...</c>). These pin
/// the HTTP contract: routes, verbs, request-body shape, and response parsing for list / create /
/// update / delete. Command parsing + exit codes are covered separately by
/// <see cref="CommandIntegrationMaintenanceTests"/>.
/// </summary>
public sealed class NodePilotApiClientMaintenanceWindowsTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly NodePilotApiClient _client;

    public NodePilotApiClientMaintenanceWindowsTests()
    {
        _server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(_server.Url + "/") };
        _client = new NodePilotApiClient(http) { BearerToken = "test-token" };
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    private static object WindowJson(Guid id, string name, string mode = "Blackout", string scope = "Global") => new
    {
        id,
        name,
        description = (string?)null,
        isEnabled = true,
        mode,
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
    public async Task ListMaintenanceWindowsAsync_ParsesRows()
    {
        _server.Given(Request.Create().WithPath("/api/maintenance-windows").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
               {
                   WindowJson(Guid.NewGuid(), "Nightly"),
                   WindowJson(Guid.NewGuid(), "FinanceFreeze", scope: "Folders"),
               }));

        var rows = await _client.ListMaintenanceWindowsAsync(CancellationToken.None);

        rows.Should().HaveCount(2);
        rows[0].Name.Should().Be("Nightly");
        rows[0].WeeklyStartMinuteOfDay.Should().Be(22 * 60);
        rows[1].ScopeKind.Should().Be("Folders");
    }

    [Fact]
    public async Task CreateMaintenanceWindowAsync_PostsBody_AndParsesResponse()
    {
        var created = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/maintenance-windows").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(201).WithBodyAsJson(WindowJson(created, "Nightly")));

        var req = new CreateMaintenanceWindowRequest(
            "Nightly", null, true, "Blackout", "Global", "Weekly",
            null, null, 0b0111110, 22 * 60, 23 * 60, null, null, "UTC", null);

        var result = await _client.CreateMaintenanceWindowAsync(req, CancellationToken.None);

        result.Id.Should().Be(created);
        result.Name.Should().Be("Nightly");

        // The serialized request body must carry the fields the backend expects.
        var captured = _server.LogEntries.Last();
        captured.RequestMessage.Method.Should().Be("POST");
        captured.RequestMessage.Body.Should().Contain("Nightly").And.Contain("Blackout");
    }

    [Fact]
    public async Task CreateMaintenanceWindowAsync_BadRequest_Throws()
    {
        _server.Given(Request.Create().WithPath("/api/maintenance-windows").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(400).WithBodyAsJson(new { message = "Cron windows require a cronExpression" }));

        var req = new CreateMaintenanceWindowRequest(
            "Cronny", null, true, "Blackout", "Global", "Cron",
            null, null, 0, null, null, null, null, "UTC", null);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            _client.CreateMaintenanceWindowAsync(req, CancellationToken.None));
        ((int)ex.StatusCode).Should().Be(400);
    }

    [Fact]
    public async Task UpdateMaintenanceWindowAsync_PutsToIdRoute()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/maintenance-windows/{id}").UsingPut())
               .RespondWith(Response.Create().WithStatusCode(204));

        var req = new UpdateMaintenanceWindowRequest(
            "Renamed", null, true, "AllowOnly", "Global", "Weekly",
            null, null, 0b0111110, 1 * 60, 4 * 60, null, null, "UTC", null);

        await _client.UpdateMaintenanceWindowAsync(id, req, CancellationToken.None);

        var captured = _server.LogEntries.Last();
        captured.RequestMessage.Method.Should().Be("PUT");
        captured.RequestMessage.Body.Should().Contain("Renamed").And.Contain("AllowOnly");
    }

    [Fact]
    public async Task UpdateMaintenanceWindowAsync_NotFound_Throws()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/maintenance-windows/{id}").UsingPut())
               .RespondWith(Response.Create().WithStatusCode(404));

        var req = new UpdateMaintenanceWindowRequest(
            "X", null, true, "Blackout", "Global", "Weekly",
            null, null, 0b0111110, 0, 1439, null, null, "UTC", null);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            _client.UpdateMaintenanceWindowAsync(id, req, CancellationToken.None));
        ((int)ex.StatusCode).Should().Be(404);
    }

    [Fact]
    public async Task DeleteMaintenanceWindowAsync_DeletesIdRoute()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/maintenance-windows/{id}").UsingDelete())
               .RespondWith(Response.Create().WithStatusCode(204));

        await _client.DeleteMaintenanceWindowAsync(id, CancellationToken.None);

        _server.LogEntries.Last().RequestMessage.Method.Should().Be("DELETE");
    }
}
