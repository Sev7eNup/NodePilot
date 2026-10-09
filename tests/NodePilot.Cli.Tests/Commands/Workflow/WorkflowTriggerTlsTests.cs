using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NodePilot.Cli.Tests.Infra;
using NodePilot.Cli.Tests.Tls;
using NodePilot.Core.Clients;
using Xunit;

namespace NodePilot.Cli.Tests.Commands.Workflow;

[Collection(CommandTestCollection.Name)]
public sealed class WorkflowTriggerTlsTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Trigger_UsesResolvedTlsPinWithoutSendingSessionBearer(bool useProfilePin, bool matchingPin)
    {
        using var certificate = TestCertificates.CreateSelfSigned("localhost", "localhost");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        var executionId = Guid.NewGuid();
        var requests = 0;
        string? receivedKey = null;
        string? receivedBearer = null;
        app.MapPost("/api/trigger/pinned-flow", (HttpRequest request) =>
        {
            requests++;
            receivedKey = request.Headers["X-Api-Key"];
            receivedBearer = request.Headers.Authorization;
            return Results.Json(new { id = executionId, workflowId = Guid.NewGuid(), status = "Pending", startedAt = DateTime.UtcNow });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var harness = new CommandTestHarness(authenticated: false, autoAllowInsecure: false);
        var server = app.Urls.Single().Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        var pin = matchingPin ? CertificatePin.Compute(certificate) : new string('A', 64);
        var config = harness.Config.Load();
        config.Profiles["default"] = new ProfileEntry { Server = server, TlsThumbprint = useProfilePin ? pin : null };
        harness.Config.Save(config);
        var args = new List<string> { "workflow", "trigger", "pinned-flow", "--api-key", "test-key", "--server", server };
        if (!useProfilePin) args.AddRange(["--tls-thumbprint", pin]);

        var result = harness.Run(args.ToArray());

        Assert.Equal(matchingPin ? ExitCodes.Success : ExitCodes.Error, result.ExitCode);
        Assert.Equal(matchingPin ? 1 : 0, requests);
        if (matchingPin)
        {
            Assert.Contains(executionId.ToString(), result.Output);
            Assert.Equal("test-key", receivedKey);
            Assert.True(string.IsNullOrEmpty(receivedBearer));
        }
    }
}
