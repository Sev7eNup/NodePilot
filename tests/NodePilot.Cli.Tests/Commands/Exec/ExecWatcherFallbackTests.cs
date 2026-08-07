using FluentAssertions;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Commands.Exec;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using NodePilot.Cli.Tests.Infra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NodePilot.Cli.Tests.Commands.Exec;

/// <summary>
/// The SignalR-to-polling fallback in <see cref="ExecWatcher.RunAsync"/>. This is the branch
/// that decides whether `np exec watch` works at all in an environment where the hub is not
/// reachable (proxy without websocket upgrade, headless CI, expired session) — it must never
/// fail the command, only degrade to polling.
/// <see cref="ExecWatcherTests"/> covers the poll loop itself.
/// </summary>
[Collection(CommandTestCollection.Name)]
public sealed class ExecWatcherFallbackTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly NodePilotApiClient _client;
    private readonly OutputWriter _writer;

    public ExecWatcherFallbackTests()
    {
        _server = WireMockServer.Start();
        _client = new NodePilotApiClient(new HttpClient { BaseAddress = new Uri(_server.Url + "/") });
        _writer = new OutputWriter(OutputFormat.Json, noColor: true);
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    [Fact]
    public async Task RunAsync_WithoutASession_FallsBackToPollingAndStillReportsTheResult()
    {
        var executionId = Guid.NewGuid();
        StubExecution(executionId, "Succeeded");
        StubSteps(executionId);

        var exitCode = await ExecWatcher.RunAsync(
            _client, EmptySession(), executionId, _writer, TestContext.Current.CancellationToken);

        exitCode.Should().Be(ExitCodes.Success,
            "a missing session must degrade to polling, not fail the watch");
    }

    [Fact]
    public async Task RunAsync_WithASessionButAnUnreachableHub_FallsBackToPolling()
    {
        var executionId = Guid.NewGuid();
        StubExecution(executionId, "Succeeded");
        StubSteps(executionId);

        // The session points at a port nothing listens on, so the hub handshake fails and the
        // watcher has to fall through to the polling loop against the (working) REST stub.
        var exitCode = await ExecWatcher.RunAsync(
            _client,
            SessionFor("http://127.0.0.1:1"),
            executionId,
            _writer,
            TestContext.Current.CancellationToken);

        exitCode.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task RunAsync_FailedExecutionViaFallback_MapsToTheRunFailedExitCode()
    {
        var executionId = Guid.NewGuid();
        StubExecution(executionId, "Failed");
        StubSteps(executionId);

        var exitCode = await ExecWatcher.RunAsync(
            _client, EmptySession(), executionId, _writer, TestContext.Current.CancellationToken);

        exitCode.Should().NotBe(ExitCodes.Success);
    }

    [Fact]
    public async Task RunAsync_ForcePolling_SkipsTheHubEntirely()
    {
        var executionId = Guid.NewGuid();
        StubExecution(executionId, "Succeeded");
        StubSteps(executionId);

        var exitCode = await ExecWatcher.RunAsync(
            _client,
            SessionFor("http://127.0.0.1:1"),
            executionId,
            _writer,
            TestContext.Current.CancellationToken,
            forcePolling: true);

        exitCode.Should().Be(ExitCodes.Success,
            "--poll must not even attempt the handshake against the dead hub");
    }

    // ---------------------------------------------------------------- helpers

    private static SessionContext EmptySession() => new() { Profile = "default" };

    private static SessionContext SessionFor(string server) => new()
    {
        Profile = "default",
        Server = server,
        Session = new StoredSession
        {
            Server = server,
            Token = "jwt-token",
            Username = "admin",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        },
    };

    private void StubExecution(Guid id, string status) =>
        _server.Given(Request.Create().WithPath($"/api/executions/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id,
                workflowId = Guid.NewGuid(),
                workflowName = "Build",
                status,
                startedAt = DateTime.UtcNow,
                completedAt = DateTime.UtcNow,
                errorMessage = (string?)null,
                triggeredBy = "cli",
                durationMs = 42,
            }));

    private void StubSteps(Guid id) =>
        _server.Given(Request.Create().WithPath($"/api/executions/{id}/steps").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));
}
