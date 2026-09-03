using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// Tests for RestApiActivity's manual redirect loop:
/// per-hop SSRF revalidation, credential-header stripping on cross-origin redirect,
/// RFC 7231 body-drop on 301/302/303, and max-redirect boundary enforcement.
/// </summary>
public class RestApiRedirectTests
{
    private static JsonElement ParseConfig(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static StepExecutionContext CreateContext() =>
        new() { WorkflowExecutionId = Guid.NewGuid(), StepId = "step-1" };

    /// <summary>Handler that returns responses from a queue in order.</summary>
    private class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _queue;
        public List<HttpRequestMessage> Requests { get; } = new();

        public SequencedHandler(IEnumerable<HttpResponseMessage> responses)
            => _queue = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_queue.Dequeue());
        }
    }

    private static HttpResponseMessage Redirect(string location, HttpStatusCode code = HttpStatusCode.Found)
    {
        var r = new HttpResponseMessage(code);
        r.Headers.Location = new Uri(location, UriKind.Absolute);
        return r;
    }

    private static HttpResponseMessage Ok(string body = "final")
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };
    }

    private static (RestApiActivity activity, SequencedHandler handler) CreateActivity(
        IEnumerable<HttpResponseMessage> responses,
        Dictionary<string, string?>? extraConfig = null)
    {
        var handler = new SequencedHandler(responses);
        var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("NodePilot")).Returns(client);

        var configEntries = new Dictionary<string, string?>
        {
            ["RestApi:AllowedHosts:0"] = "192.0.2.10",
            ["RestApi:AllowedHosts:1"] = "192.0.2.20",
        };
        if (extraConfig != null)
            foreach (var kv in extraConfig) configEntries[kv.Key] = kv.Value;

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(configEntries).Build();
        var provider = new RestApiHttpClientProvider(factory.Object, cfg);
        return (new RestApiActivity(provider, cfg), handler);
    }

    [Fact]
    public async Task AuthorSuppliedContentType_LandsOnTheEntity()
    {
        // The entity's media type was hard-coded to application/json and author headers went to
        // req.Headers (the REQUEST-header collection), which cannot override a content header. A
        // form-encoded token request or a SOAP call was therefore unsendable — while the designer's
        // headers placeholder tells authors to set Content-Type exactly there.
        var (activity, handler) = CreateActivity([Ok()]);

        var result = await activity.ExecuteAsync(
            CreateContext(),
            ParseConfig(
                "{\"url\": \"https://192.0.2.10/token\", \"method\": \"POST\", " +
                "\"headers\": \"Content-Type: application/x-www-form-urlencoded\", " +
                "\"body\": \"grant_type=client_credentials\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        var sent = handler.Requests.Single();
        sent.Content!.Headers.ContentType!.MediaType.Should().Be("application/x-www-form-urlencoded");
        // NonValidated, because Contains() rejects a content-header name outright. Before the fix
        // TryAddWithoutValidation stored it here as a custom header, emitting a second,
        // conflicting Content-Type field on the wire.
        sent.Headers.NonValidated.Contains("Content-Type").Should().BeFalse(
            "a content header must not also be emitted in the request-header block");
    }

    [Fact]
    public async Task WithoutAContentTypeHeader_TheBodyStaysJson()
    {
        var (activity, handler) = CreateActivity([Ok()]);

        var result = await activity.ExecuteAsync(
            CreateContext(),
            ParseConfig(
                "{\"url\": \"https://192.0.2.10/api\", \"method\": \"POST\", \"body\": \"{}\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.Requests.Single().Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task Redirect_302_FollowsToFinalLocation()
    {
        var (activity, handler) = CreateActivity(
            new[]
            {
                Redirect("https://192.0.2.20/final"),
                Ok("final response")
            },
            new Dictionary<string, string?>
            {
                ["RestApi:Proxy:Enabled"] = "true",
                ["RestApi:Proxy:Address"] = "http://proxy.example:8080",
            });

        var result = await activity.ExecuteAsync(
            CreateContext(),
            ParseConfig("{\"url\": \"https://192.0.2.10/start\", \"method\": \"GET\"}"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("final response");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Proxy_InitialDestinationMustBeExplicitlyAllowlisted()
    {
        var (activity, handler) = CreateActivity(
            [Ok()],
            new Dictionary<string, string?>
            {
                ["RestApi:Proxy:Enabled"] = "true",
                ["RestApi:Proxy:Address"] = "http://proxy.example:8080",
            });

        var result = await activity.ExecuteAsync(
            CreateContext(),
            ParseConfig("{\"url\": \"https://192.0.2.30/start\", \"method\": \"GET\"}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("not explicitly allowed");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Proxy_RedirectDestinationMustBeExplicitlyAllowlisted()
    {
        var (activity, handler) = CreateActivity(
            [Redirect("https://192.0.2.30/blocked"), Ok("must not be sent")],
            new Dictionary<string, string?>
            {
                ["RestApi:Proxy:Enabled"] = "true",
                ["RestApi:Proxy:Address"] = "http://proxy.example:8080",
            });

        var result = await activity.ExecuteAsync(
            CreateContext(),
            ParseConfig("{\"url\": \"https://192.0.2.10/start\", \"method\": \"GET\"}"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("not explicitly allowed");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Redirect_MaxRedirectsExceeded_ReturnsFailureOrLastResponse()
    {
        // 6 redirects in a row — exceeds MaxRedirects=5, so the last redirect response is returned
        var responses = Enumerable
            .Range(0, 6)
            .Select(_ => Redirect("https://192.0.2.10/loop"))
            .Cast<HttpResponseMessage>()
            .Append(Ok("should not reach"))
            .ToArray();

        var (activity, _) = CreateActivity(responses);

        var result = await activity.ExecuteAsync(
            CreateContext(),
            ParseConfig("{\"url\": \"https://192.0.2.10/start\", \"method\": \"GET\"}"),
            CancellationToken.None);

        // After 5 followed redirects the 6th redirect response is returned as-is (302,
        // Success=false)
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task CrossOriginRedirect_StripsAuthorizationHeader()
    {
        var (activity, handler) = CreateActivity(new[]
        {
            Redirect("https://192.0.2.20/secure"),
            Ok("ok")
        });

        var config = ParseConfig("""
            {
              "url": "https://192.0.2.10/start",
              "method": "GET",
              "headers": { "Authorization": "Bearer secret-token" }
            }
            """);

        await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        // First request (to 192.0.2.10) should carry Authorization
        handler.Requests[0].Headers.Authorization.Should().NotBeNull();
        // Second request (to 192.0.2.20) must not carry Authorization: cross-origin
        handler.Requests[1].Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task CrossOriginRedirect_StripsUnknownCustomHeader_ButKeepsPublicHeader()
    {
        var (activity, handler) = CreateActivity(new[]
        {
            Redirect("https://192.0.2.20/secure"),
            Ok("ok")
        });

        var config = ParseConfig("""
            {
              "url": "https://192.0.2.10/start",
              "method": "GET",
              "headers": {
                "X-Tenant-Token": "opaque-tenant-credential",
                "Accept": "application/json"
              }
            }
            """);

        await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        handler.Requests[0].Headers.Contains("X-Tenant-Token").Should().BeTrue();
        handler.Requests[1].Headers.Contains("X-Tenant-Token").Should().BeFalse();
        handler.Requests[1].Headers.Accept.Should().ContainSingle(v => v.MediaType == "application/json");
    }

    [Fact]
    public async Task CrossOriginRedirect_StripsUnknownCustomHeader_FromTextForm()
    {
        var (activity, handler) = CreateActivity(new[]
        {
            Redirect("https://192.0.2.20/secure"),
            Ok("ok")
        });
        var config = ParseConfig("""
            {
              "url": "https://192.0.2.10/start",
              "method": "GET",
              "headers": "X_Tenant.Token: opaque-tenant-credential\nAccept: application/json"
            }
            """);

        await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        handler.Requests[0].Headers.Contains("X_Tenant.Token").Should().BeTrue();
        handler.Requests[1].Headers.Contains("X_Tenant.Token").Should().BeFalse();
        handler.Requests[1].Headers.Accept.Should().ContainSingle(v => v.MediaType == "application/json");
    }

    [Fact]
    public async Task SameOriginRedirect_KeepsAuthorizationHeader()
    {
        // Same host, different path — credentials should be forwarded
        var (activity, handler) = CreateActivity(new[]
        {
            Redirect("https://192.0.2.10/page2"),
            Ok("ok")
        });

        var config = ParseConfig("""
            {
              "url": "https://192.0.2.10/page1",
              "method": "GET",
              "headers": { "Authorization": "Bearer secret-token" }
            }
            """);

        await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        // Both requests to 192.0.2.10 — Authorization must be retained
        handler.Requests[1].Headers.Authorization.Should().NotBeNull();
    }

    [Fact]
    public async Task Redirect_303_DropsPostBodyAndSwitchesToGet()
    {
        var (activity, handler) = CreateActivity(new[]
        {
            Redirect("https://192.0.2.10/result", HttpStatusCode.SeeOther),
            Ok("ok")
        });

        var config = ParseConfig("""
            {
              "url": "https://192.0.2.10/submit",
              "method": "POST",
              "body": { "data": "value" }
            }
            """);

        await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        // After 303, method must be GET and body must be absent
        handler.Requests[1].Method.Should().Be(HttpMethod.Get);
        handler.Requests[1].Content.Should().BeNull();
    }

    [Fact]
    public async Task Redirect_301_DropsBodyOnRedirect()
    {
        var (activity, handler) = CreateActivity(new[]
        {
            Redirect("https://192.0.2.10/new", HttpStatusCode.MovedPermanently),
            Ok("ok")
        });

        var config = ParseConfig("""
            {
              "url": "https://192.0.2.10/old",
              "method": "POST",
              "body": "some payload"
            }
            """);

        await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        // 301 with POST switches to GET on redirect, no body
        handler.Requests[1].Method.Should().Be(HttpMethod.Get);
        handler.Requests[1].Content.Should().BeNull();
    }

    [Fact]
    public async Task Redirect_300WithLocation_IsNotFollowed()
    {
        var (activity, handler) = CreateActivity(new[]
        {
            Redirect("https://192.0.2.20/unexpected", HttpStatusCode.MultipleChoices),
            Ok("must not be sent")
        });

        var result = await activity.ExecuteAsync(
            CreateContext(),
            ParseConfig("""
                {
                  "url": "https://192.0.2.10/start",
                  "method": "POST",
                  "body": { "credential": "body-secret" }
                }
                """),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        handler.Requests.Should().ContainSingle(
            "only the explicitly supported 301/302/303/307/308 redirect statuses may be followed");
    }

    [Theory]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task CrossOriginRedirect_307Or308_DropsSensitiveBody(HttpStatusCode statusCode)
    {
        var (activity, handler) = CreateActivity(new[]
        {
            Redirect("https://192.0.2.20/other-origin", statusCode),
            Ok("ok")
        });

        await activity.ExecuteAsync(
            CreateContext(),
            ParseConfig("""
                {
                  "url": "https://192.0.2.10/start",
                  "method": "POST",
                  "body": { "credential": "body-secret" }
                }
                """),
            CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Method.Should().Be(HttpMethod.Post);
        handler.Requests[1].Content.Should().BeNull();
    }
}
