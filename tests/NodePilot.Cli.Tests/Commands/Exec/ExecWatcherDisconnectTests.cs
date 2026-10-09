using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Commands.Exec;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using NodePilot.Cli.Tests.Infra;
using NodePilot.Core.Clients;
using Xunit;

namespace NodePilot.Cli.Tests.Commands.Exec;

[Collection(CommandTestCollection.Name)]
public sealed class ExecWatcherDisconnectTests
{
    [Fact]
    public async Task ConnectedWatcher_WhenHubCloses_PollsTheExistingExecutionToCompletion()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        var state = new WatchState();
        builder.Services.AddSingleton(state);
        await using var app = builder.Build();
        var executionId = Guid.NewGuid();
        app.MapHub<DisconnectHub>("/hubs/execution");
        app.MapGet($"/api/executions/{executionId}", () =>
        {
            var read = Interlocked.Increment(ref state.Reads);
            if (read == 1) state.CatchUpRead.TrySetResult();
            return Results.Json(new
            {
                id = executionId,
                workflowId = Guid.NewGuid(),
                status = read == 1 ? "Running" : "Succeeded",
                startedAt = DateTime.UtcNow,
                completedAt = read == 1 ? (DateTime?)null : DateTime.UtcNow,
            });
        });
        app.MapGet($"/api/executions/{executionId}/steps", () => Results.Json(Array.Empty<object>()));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var server = app.Urls.Single();
        using var http = new HttpClient { BaseAddress = new Uri(server + "/") };
        var api = new NodePilotApiClient(http);
        var session = new SessionContext
        {
            Profile = "test",
            Server = server,
            Session = new StoredSession
            {
                Server = server,
                Token = "test-token",
                Username = "test",
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            },
        };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var watching = ExecWatcher.RunAsync(api, session, executionId,
            new OutputWriter(OutputFormat.Json, noColor: true), stop.Token);
        try
        {
            await state.CatchUpRead.Task.WaitAsync(TimeSpan.FromSeconds(10), stop.Token);
            // The connection and group join succeeded. Lose the stream only after the REST
            // catch-up observed Running, so initial-connect fallback cannot satisfy this test.
            state.Connection!.Abort();
            var result = await watching.WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
            result.Should().Be(ExitCodes.Success);
            state.Reads.Should().BeGreaterThanOrEqualTo(2);
        }
        finally
        {
            await stop.CancelAsync();
            await watching.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    public sealed class WatchState
    {
        public HubCallerContext? Connection;
        public int Reads;
        public TaskCompletionSource CatchUpRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class DisconnectHub(WatchState state) : Hub
    {
        public Task JoinExecution(string executionId)
        {
            state.Connection = Context;
            return Task.CompletedTask;
        }
    }
}
