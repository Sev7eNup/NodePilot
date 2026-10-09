using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Commands.Exec;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using NodePilot.Cli.Tests.Infra;
using NodePilot.Cli.Tests.Tls;
using NodePilot.Core.Clients;
using Xunit;

namespace NodePilot.Cli.Tests.Commands.Exec;

[Collection(CommandTestCollection.Name)]
public sealed class ExecWatcherTlsTests
{
    [Theory]
    [InlineData("matching-pin", true)]
    [InlineData("insecure", true)]
    [InlineData("wrong-pin-insecure", false)]
    [InlineData("stock", false)]
    public async Task Watch_SelfSignedServer_UsesSessionTrustForNegotiationAndWebSocket(string trust, bool expectedJoin)
    {
        using var certificate = TestCertificates.CreateSelfSigned("localhost", "localhost");
        var pin = CertificatePin.Compute(certificate);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            endpoint => endpoint.UseHttps(certificate)));
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        var state = new WatchState();
        builder.Services.AddSingleton(state);
        await using var app = builder.Build();
        // No long-poll/SSE fallback can hide a missing WebSocket certificate callback.
        app.MapHub<TlsHub>("/hubs/execution", options => options.Transports = HttpTransportType.WebSockets);
        var executionId = Guid.NewGuid();
        app.MapGet($"/api/executions/{executionId}", () => Results.Json(new
        {
            id = executionId, workflowId = Guid.NewGuid(), status = "Succeeded",
            startedAt = DateTime.UtcNow, completedAt = DateTime.UtcNow,
        }));
        app.MapGet($"/api/executions/{executionId}/steps", () => Results.Json(Array.Empty<object>()));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var server = app.Urls.Single();
        using var http = new HttpClient(PinnedCertificateHandlerFactory.Create(new ClientTlsOptions(pin)))
        {
            BaseAddress = new Uri(server + "/"),
        };
        var session = new SessionContext
        {
            Profile = "test", Server = server,
            Tls = trust switch
            {
                "matching-pin" => new ClientTlsOptions(pin),
                "insecure" => new ClientTlsOptions(SkipVerification: true),
                "wrong-pin-insecure" => new ClientTlsOptions(new string('A', 64), SkipVerification: true),
                _ => ClientTlsOptions.None,
            },
            Session = new StoredSession
            {
                Server = server, Token = "test-token", Username = "test", ExpiresAt = DateTime.UtcNow.AddHours(1),
            },
        };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(15));

        var result = await ExecWatcher.RunAsync(new NodePilotApiClient(http), session, executionId,
            new OutputWriter(OutputFormat.Json, noColor: true), stop.Token);

        result.Should().Be(ExitCodes.Success);
        state.Joined.Should().Be(expectedJoin,
            "permitted TLS must support live follow, while rejected Hub TLS may only fall back to the independently trusted REST client");
        if (expectedJoin) state.UsedWebSocket.Should().BeTrue();
    }

    public sealed class WatchState
    {
        public bool Joined;
        public bool UsedWebSocket;
    }

    public sealed class TlsHub(WatchState state) : Hub
    {
        public void JoinExecution(string executionId)
        {
            state.UsedWebSocket = Context.GetHttpContext()!.WebSockets.IsWebSocketRequest;
            state.Joined = true;
        }
    }
}
