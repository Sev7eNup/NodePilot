using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Notifications;
using NodePilot.Engine.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Security;

/// <summary>
/// The call sites must ask NetworkGuard to check every resolved address when a proxy carries
/// the request, and only one when the request goes direct. A host that mixes a routable and a
/// link-local record tells the two apart.
/// </summary>
public class ProxiedSsrfCallSiteTests
{
    private const string MixedHost = "mixed.test";
    private const string CleanHost = "clean.test";

    private static IPAddress[] Resolve(string host) => host switch
    {
        MixedHost => [IPAddress.Parse("203.0.113.10"), IPAddress.Parse("169.254.169.254")],
        CleanHost => [IPAddress.Parse("203.0.113.20")],
        _ => throw new InvalidOperationException($"unexpected lookup for '{host}'"),
    };

    private static IConfiguration BuildConfig(bool bypassProxy)
    {
        var entries = new Dictionary<string, string?>
        {
            ["RestApi:Proxy:Enabled"] = "true",
            ["RestApi:Proxy:Address"] = "http://proxy.example:8080",
            // Allow-listed so the proxy destination policy passes and only the address rule decides.
            ["RestApi:AllowedHosts:0"] = MixedHost,
            ["RestApi:AllowedHosts:1"] = CleanHost,
        };
        if (bypassProxy)
        {
            entries["RestApi:Proxy:BypassList:0"] = MixedHost;
            entries["RestApi:Proxy:BypassList:1"] = CleanHost;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
    }

    private static RestApiHttpClientProvider BuildProvider(IConfiguration config, HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("NodePilot")).Returns(new HttpClient(handler));
        return new RestApiHttpClientProvider(factory.Object, config);
    }

    private static StepExecutionContext CreateContext() =>
        new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1" };

    private static JsonElement StepConfig(string url) =>
        JsonDocument.Parse($"{{\"url\": \"{url}\", \"method\": \"GET\"}}").RootElement;

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.Absolute);
        return response;
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent("ok") };

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RestApi_InitialUrl_MixedAddresses_BlockedOnlyWhenProxied(bool proxied, bool expectSuccess)
    {
        NetworkGuard.HostResolverOverride.Value = Resolve;
        var handler = new StubHttpMessageHandler(_ => Ok());
        var config = BuildConfig(bypassProxy: !proxied);
        var activity = new RestApiActivity(BuildProvider(config, handler), config);

        var result = await activity.ExecuteAsync(
            CreateContext(), StepConfig($"http://{MixedHost}/api"), TestContext.Current.CancellationToken);

        result.Success.Should().Be(expectSuccess);
        if (proxied)
        {
            result.ErrorOutput.Should().Contain("link-local");
            handler.Requests.Should().BeEmpty();
        }
        else
        {
            handler.Requests.Should().ContainSingle();
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RestApi_RedirectHop_MixedAddresses_BlockedOnlyWhenProxied(bool proxied, bool expectSuccess)
    {
        NetworkGuard.HostResolverOverride.Value = Resolve;
        var responses = new Queue<HttpResponseMessage>([Redirect($"http://{MixedHost}/next"), Ok()]);
        var handler = new StubHttpMessageHandler(_ => responses.Dequeue());
        var config = BuildConfig(bypassProxy: !proxied);
        var activity = new RestApiActivity(BuildProvider(config, handler), config);

        var result = await activity.ExecuteAsync(
            CreateContext(), StepConfig($"http://{CleanHost}/start"), TestContext.Current.CancellationToken);

        result.Success.Should().Be(expectSuccess);
        if (proxied)
        {
            result.ErrorOutput.Should().Contain("link-local");
            handler.Requests.Should().ContainSingle();
        }
        else
        {
            handler.Requests.Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task WebhookSink_Proxied_MixedAddresses_ReturnsBlockedFailure()
    {
        NetworkGuard.HostResolverOverride.Value = Resolve;
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var config = BuildConfig(bypassProxy: false);
        var sink = new WebhookNotificationSink(BuildProvider(config, handler), config);

        var result = await sink.SendAsync(
            SampleContext(), $"http://{MixedHost}/hook", secret: null, TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.Error.Should().StartWith("Blocked webhook URL: ").And.Contain("link-local");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task WebhookSink_Bypassed_MixedAddresses_Delivers()
    {
        NetworkGuard.HostResolverOverride.Value = Resolve;
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var config = BuildConfig(bypassProxy: true);
        var sink = new WebhookNotificationSink(BuildProvider(config, handler), config);

        var result = await sink.SendAsync(
            SampleContext(), $"http://{MixedHost}/hook", secret: null, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
    }

    private static NotificationContext SampleContext() => new(
        EventType: NotificationEventType.ExecutionFailed,
        Severity: NotificationSeverity.Warning,
        EventKey: "exec:abc:ExecutionFailed",
        WorkflowId: Guid.NewGuid(),
        WorkflowName: "Nightly Backup",
        FolderId: null,
        FolderPath: "/ops",
        ExecutionId: Guid.NewGuid(),
        Status: "Failed",
        ErrorMessage: "disk full",
        DurationMs: 4200,
        OccurredAt: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        TriggeredBy: "scheduler",
        CallDepth: 0,
        IsSubWorkflow: false,
        TargetMachine: null,
        SourceKey: null,
        Title: null,
        Summary: null,
        DeepLinkPath: "/executions/x");
}
