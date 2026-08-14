using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Options;
using NodePilot.Engine.Security;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Engine.Tests.Activities;

public class DelayActivityTests
{
    private readonly DelayActivity _activity = new();

    private static JsonElement ParseConfig(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static StepExecutionContext CreateContext() =>
        new()
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "step-1"
        };

    [Fact]
    public async Task ExecuteAsync_CustomSeconds_ReturnsCorrectOutput()
    {
        var config = ParseConfig("{\"seconds\": 1}");

        var result = await _activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("1");
        result.Output.Should().Contain("second");
    }

    [Fact]
    public async Task ExecuteAsync_DefaultSeconds_OutputMentionsFiveSeconds()
    {
        var config = ParseConfig("{}");

        var result = await _activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("5");
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_ThrowsOperationCancelledException()
    {
        var config = ParseConfig("{\"seconds\": 30}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _activity.ExecuteAsync(CreateContext(), config, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

public class RestApiActivityTests
{
    private static JsonElement ParseConfig(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static StepExecutionContext CreateContext() =>
        new()
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "step-1"
        };

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public MockHttpMessageHandler(HttpResponseMessage response) => _response = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(ct);
            return _response;
        }
    }

    private static (RestApiActivity activity, MockHttpMessageHandler handler) CreateActivity(HttpResponseMessage response)
    {
        var handler = new MockHttpMessageHandler(response);
        var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("NodePilot")).Returns(client);
        // TEST-NET-1 is a stable non-private literal, so these unit tests never depend on
        // external DNS while still exercising the real URL policy.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RestApi:AllowedHosts:0"] = "192.0.2.10",
            })
            .Build();
        var provider = new RestApiHttpClientProvider(factory.Object, cfg);
        return (new RestApiActivity(provider, cfg), handler);
    }

    [Fact]
    public async Task RestApi_GetRequest_ReturnsResponseBody()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true}")
        };
        var (activity, _) = CreateActivity(response);
        var config = ParseConfig("{\"url\": \"https://192.0.2.10/data\", \"method\": \"GET\"}");

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("200");
        result.Output.Should().Contain("{\"ok\":true}");
        result.OutputParameters["statusCode"].Should().Be("200");
    }

    [Fact]
    public async Task RestApi_PostWithBody_SendsJsonBody()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"id\":1}")
        };
        var (activity, handler) = CreateActivity(response);
        var config = ParseConfig("{\"url\": \"https://192.0.2.10/data\", \"method\": \"POST\", \"body\": {\"name\": \"test\"}}");

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("201");
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequestBody.Should().Contain("\"name\"");
        handler.LastRequestBody.Should().Contain("\"test\"");
    }

    [Fact]
    public async Task RestApi_CustomHeaders_SetsHeaders()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("ok")
        };
        var (activity, handler) = CreateActivity(response);
        var config = ParseConfig("{\"url\": \"https://192.0.2.10/data\", \"method\": \"GET\", \"headers\": {\"X-Api-Key\": \"secret123\"}}");

        await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        handler.LastRequest!.Headers.GetValues("X-Api-Key").Should().Contain("secret123");
    }

    [Fact]
    public async Task RestApi_FailedRequest_ReturnsErrorResult()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Internal Server Error")
        };
        var (activity, _) = CreateActivity(response);
        var config = ParseConfig("{\"url\": \"https://192.0.2.10/data\", \"method\": \"GET\"}");

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Output.Should().Contain("500");
        result.ErrorOutput.Should().Be("Internal Server Error");
        result.OutputParameters["statusCode"].Should().Be("500");
    }

    [Fact]
    public async Task RestApi_OversizedBodyFailure_StillReturnsStatusCodeParameter()
    {
        var response = new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
        {
            Content = new ByteArrayContent(new byte[16 * 1024 * 1024 + 1])
        };
        var (activity, _) = CreateActivity(response);
        var config = ParseConfig("{\"url\": \"https://192.0.2.10/data\", \"method\": \"GET\"}");

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("response body exceeded");
        result.OutputParameters["statusCode"].Should().Be("413");
    }
}

public class EmailActivityTests
{
    private static JsonElement ParseConfig(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static StepExecutionContext CreateContext() =>
        new()
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "step-1"
        };

    private static IOptionsMonitor<SmtpOptions> SmtpConfig() =>
        new StaticOptionsMonitor<SmtpOptions>(new SmtpOptions
        {
            Host = "localhost",
            Port = 59999,
            From = "test@localhost",
        });

    [Fact]
    public async Task Email_SmtpFailure_ReturnsErrorResult()
    {
        var activity = new EmailActivity(SmtpConfig());
        var config = ParseConfig("{\"to\": \"admin@test.com\", \"subject\": \"Test\", \"body\": \"Hello\"}");

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Email_SendHangs_ResolvesAsFailureWithinTimeout_DoesNotHang()
    {
        // Regression (stuck-Running root cause): System.Net.Mail.SmtpClient's cancellation is
        // racy — a connect that black-holes can leave the returned Task unresolved, which parks
        // the engine scheduler on a never-completing step and strands the whole execution in
        // Running. The WaitAsync bound MUST resolve the step within the timeout regardless.
        // 192.0.2.1 is TEST-NET-1 (RFC 5737) — a guaranteed non-routable connect black-hole.
        // If the fix were missing, this test would hang instead of returning.
        var activity = new EmailActivity(new StaticOptionsMonitor<SmtpOptions>(new SmtpOptions
        {
            Host = "192.0.2.1",
            Port = 25,
            From = "test@localhost",
            EnableSsl = false,
        }));
        var config = ParseConfig("{\"to\": \"admin@test.com\", \"subject\": \"T\", \"body\": \"B\", \"timeoutSeconds\": 1}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);
        sw.Stop();

        // Resolved as a failed step (the fact that this line is reached proves it did not hang).
        result.Success.Should().BeFalse();
        // Bounded: the WaitAsync ceiling (1s) fires well before the OS connect timeout (~21s).
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Email_RunCancelled_PropagatesCancellation_NotFailure()
    {
        // A run-level cancel must surface as OperationCanceledException (engine -> Cancelled),
        // never be swallowed into a Failed step. The WaitAsync honors the outer token.
        var activity = new EmailActivity(new StaticOptionsMonitor<SmtpOptions>(new SmtpOptions
        {
            Host = "192.0.2.1",
            Port = 25,
            From = "test@localhost",
            EnableSsl = false,
        }));
        var config = ParseConfig("{\"to\": \"admin@test.com\", \"subject\": \"T\", \"body\": \"B\", \"timeoutSeconds\": 30}");
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var act = async () => await activity.ExecuteAsync(CreateContext(), config, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("a@b.c, attacker@evil.com")]
    [InlineData("a@b.c;attacker@evil.com")]
    public async Task Email_MultipleRecipients_Rejected(string to)
    {
        var activity = new EmailActivity(SmtpConfig());
        var config = ParseConfig($"{{\"to\": \"{to}\", \"subject\": \"Test\", \"body\": \"Hello\"}}");

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("single recipient");
    }

    [Theory]
    [InlineData("a@b.c\r\nBcc: attacker@evil.com")]
    [InlineData("a@b.c\nBcc: attacker@evil.com")]
    public async Task Email_NewlineInRecipient_Rejected(string to)
    {
        var activity = new EmailActivity(SmtpConfig());
        var config = ParseConfig(System.Text.Json.JsonSerializer.Serialize(new
        {
            to, subject = "Test", body = "Hello"
        }));

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("newline");
    }

    [Fact]
    public async Task Email_NewlineInSubject_Rejected()
    {
        var activity = new EmailActivity(SmtpConfig());
        var config = ParseConfig(System.Text.Json.JsonSerializer.Serialize(new
        {
            to = "admin@test.com",
            subject = "Hi\r\nBcc: attacker@evil.com",
            body = "Hello",
        }));

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("newline");
    }

    [Fact]
    public async Task Email_MissingTo_ReturnsFailure()
    {
        var activity = new EmailActivity(SmtpConfig());
        var config = ParseConfig("{\"subject\": \"Test\", \"body\": \"Hello\"}");

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("'to' is required");
    }

    [Fact]
    public async Task Email_EmptyTo_ReturnsFailure()
    {
        var activity = new EmailActivity(SmtpConfig());
        var config = ParseConfig("{\"to\": \"   \", \"subject\": \"Test\", \"body\": \"Hello\"}");

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("'to' is required");
    }

    [Fact]
    public async Task Email_HappyPath_ReturnsSuccessAndDeliversToServer()
    {
        await using var smtp = await FakeSmtpServer.StartAsync();
        var activity = new EmailActivity(new StaticOptionsMonitor<SmtpOptions>(new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = smtp.Port,
            From = "nodepilot@localhost",
            // FakeSmtpServer is plaintext-only — explicitly opt out of the EnableSsl=true
            // default introduced by H-2 (audit 2026-05-15). Production code path still
            // gets the secure default; this fixture mirrors the "localhost relay without
            // TLS" legitimate use-case.
            EnableSsl = false,
        }));

        var config = ParseConfig(System.Text.Json.JsonSerializer.Serialize(new
        {
            to = "ops@example.com",
            subject = "Hello",
            body = "World",
        }));

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("Email sent to ops@example.com");
        // Confirms we got past validation and actually completed an SMTP transaction.
        var session = await smtp.AwaitSessionAsync(TimeSpan.FromSeconds(5));
        session.MailFrom.Should().Contain("nodepilot@localhost");
        session.RcptTo.Should().Contain("ops@example.com");
        session.DataReceived.Should().BeTrue();
    }

    [Fact]
    public async Task Email_HtmlBody_DeliversWithHtmlContentType()
    {
        await using var smtp = await FakeSmtpServer.StartAsync();
        var activity = new EmailActivity(new StaticOptionsMonitor<SmtpOptions>(new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = smtp.Port,
            From = "nodepilot@localhost",
            EnableSsl = false, // FakeSmtpServer plaintext-only — see Email_HappyPath_… comment.
        }));

        var config = ParseConfig(System.Text.Json.JsonSerializer.Serialize(new
        {
            to = "ops@example.com",
            subject = "Hello",
            body = "<b>Hi</b>",
            isHtml = true,
        }));

        var result = await activity.ExecuteAsync(CreateContext(), config, CancellationToken.None);

        result.Success.Should().BeTrue();
        var session = await smtp.AwaitSessionAsync(TimeSpan.FromSeconds(5));
        session.DataReceived.Should().BeTrue();
        session.DataPayload.Should().Contain("text/html");
    }
}
