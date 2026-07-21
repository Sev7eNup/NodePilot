using FluentAssertions;
using NodePilot.Cli;
using NodePilot.Cli.Api;
using NodePilot.Cli.Commands.Exec;
using NodePilot.Cli.Commands.Workflow;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using NodePilot.Cli.Tests.Infra;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// Polling fallback path of the exec watcher. The SignalR path needs a live hub which
/// we don't spin up here; the polling loop is what actually runs in CI/headless
/// environments anyway, and it's the more failure-prone branch (terminal-status detection,
/// step-dedup set, exit-code mapping).
/// </summary>
[Collection(CommandTestCollection.Name)]
public sealed class ExecWatcherTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly NodePilotApiClient _client;
    private readonly OutputWriter _writer;

    public ExecWatcherTests()
    {
        _server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(_server.Url + "/") };
        _client = new NodePilotApiClient(http);
        _writer = new OutputWriter(OutputFormat.Json, noColor: true);
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    [Fact]
    public async Task PollLoop_Succeeds_ReturnsSuccessExitCode()
    {
        var execId = Guid.NewGuid();
        StubProgression(execId, "Running", "Succeeded");
        StubSteps(execId);

        var rc = await ExecWatcher.PollLoopAsync(_client, execId, _writer, CancellationToken.None);
        rc.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public async Task PollLoop_Fails_ReturnsRunFailedExitCode()
    {
        var execId = Guid.NewGuid();
        StubProgression(execId, "Running", "Failed");
        StubSteps(execId);

        var rc = await ExecWatcher.PollLoopAsync(_client, execId, _writer, CancellationToken.None);
        rc.Should().Be(ExitCodes.RunFailed);
    }

    [Fact]
    public async Task PollLoop_Cancelled_ReturnsRunFailedExitCode()
    {
        var execId = Guid.NewGuid();
        StubProgression(execId, "Cancelled");
        StubSteps(execId);

        var rc = await ExecWatcher.PollLoopAsync(_client, execId, _writer, CancellationToken.None);
        rc.Should().Be(ExitCodes.RunFailed);
    }

    [Fact]
    public async Task PollLoop_RendersEachStepOnce_AcrossPolls()
    {
        // Run stays Running for one poll (so steps render) then turns Succeeded. Steps are
        // returned on every poll; the watcher's seen-set must render each exactly once.
        var execId = Guid.NewGuid();
        StubProgression(execId, "Running", "Succeeded");

        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        _server
            .Given(Request.Create().WithPath($"/api/executions/{execId}/steps").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new object[]
            {
                new { id = s1, stepId = "step-1", stepName = "Check Disk", stepType = "runScript",
                      targetMachine = (string?)null, status = "Succeeded",
                      startedAt = DateTime.UtcNow, completedAt = DateTime.UtcNow,
                      output = (string?)null, errorOutput = (string?)null, attemptCount = 1,
                      pausedAt = (DateTime?)null, variablesSnapshot = (string?)null, traceOutput = (string?)null },
                new { id = s2, stepId = "step-2", stepName = "Send Mail", stepType = "emailNotification",
                      targetMachine = (string?)null, status = "Succeeded",
                      startedAt = DateTime.UtcNow, completedAt = DateTime.UtcNow,
                      output = (string?)null, errorOutput = (string?)null, attemptCount = 1,
                      pausedAt = (DateTime?)null, variablesSnapshot = (string?)null, traceOutput = (string?)null },
            }));

        var rc = await ExecWatcher.PollLoopAsync(_client, execId, _writer, CancellationToken.None);
        rc.Should().Be(ExitCodes.Success);
    }

    [Fact]
    public void IsTerminal_ClassifiesStatuses()
    {
        WorkflowRunCommand.IsTerminal("Succeeded").Should().BeTrue();
        WorkflowRunCommand.IsTerminal("Failed").Should().BeTrue();
        WorkflowRunCommand.IsTerminal("Cancelled").Should().BeTrue();
        WorkflowRunCommand.IsTerminal("Skipped").Should().BeTrue();
        WorkflowRunCommand.IsTerminal("Running").Should().BeFalse();
        WorkflowRunCommand.IsTerminal("Pending").Should().BeFalse();
        WorkflowRunCommand.IsTerminal("Paused").Should().BeFalse();
    }

    [Fact]
    public void ExitCodeFor_MapsTerminalStatusesToExitCodes()
    {
        WorkflowRunCommand.ExitCodeFor("Succeeded").Should().Be(ExitCodes.Success);
        WorkflowRunCommand.ExitCodeFor("Failed").Should().Be(ExitCodes.RunFailed);
        WorkflowRunCommand.ExitCodeFor("Cancelled").Should().Be(ExitCodes.RunFailed);
        WorkflowRunCommand.ExitCodeFor("Skipped").Should().Be(ExitCodes.Error);
    }

    private void StubProgression(Guid execId, params string[] statuses)
    {
        // WireMock state machine: each call to /api/executions/{id} flips to the next status.
        // The first mapping has NO WhenStateIs — it's the implicit initial state of the scenario.
        var scenario = "exec-progress-" + execId;
        for (int i = 0; i < statuses.Length; i++)
        {
            var builder = _server
                .Given(Request.Create().WithPath($"/api/executions/{execId}").UsingGet())
                .InScenario(scenario)
                .WillSetStateTo("step-" + (i + 1));
            if (i > 0) builder = builder.WhenStateIs("step-" + i);
            builder.RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                id = execId, workflowId = Guid.NewGuid(), status = statuses[i],
                startedAt = DateTime.UtcNow, completedAt = (DateTime?)null,
                triggeredBy = "test", errorMessage = (string?)null,
            }));
        }
    }

    private void StubSteps(Guid execId)
    {
        _server
            .Given(Request.Create().WithPath($"/api/executions/{execId}/steps").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));
    }
}
