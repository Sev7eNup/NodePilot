using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Ai.Agents;
using NodePilot.Core.Agents;
using Xunit;

namespace NodePilot.Ai.Tests;

public sealed class AgentModelTimeoutTests
{
    [Fact]
    public async Task ConcurrentRetriesPreserveAnswerAndFinalReportReservations()
    {
        var budget = new AgentBudget(5, 10, 0);
        budget.ReserveFinalReport();
        var admitted = new System.Collections.Concurrent.ConcurrentBag<int>();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => {
            if (budget.TryTakeModelRetry(true, out var number)) admitted.Add(number);
        }, TestContext.Current.CancellationToken)));
        Assert.Equal(new[] { 1, 2, 3 }, admitted.Order());
        Assert.Equal(1, budget.RemainingModelCalls);
        budget.BeginFinalReport();
        Assert.Equal(2, budget.RemainingModelCalls);
    }

    [Theory]
    [InlineData(LlmErrorKind.Timeout, null)]
    [InlineData(LlmErrorKind.RateLimited, 429)]
    [InlineData(LlmErrorKind.UpstreamError, 408)]
    [InlineData(LlmErrorKind.UpstreamError, 500)]
    [InlineData(LlmErrorKind.UpstreamError, 502)]
    [InlineData(LlmErrorKind.UpstreamError, 503)]
    [InlineData(LlmErrorKind.UpstreamError, 504)]
    [InlineData(LlmErrorKind.Unreachable, null)]
    public async Task TransientFailureRetriesSameConversationWithoutReexecutingTools(LlmErrorKind kind, int? status)
    {
        var requests = new List<LlmRequest>();
        var events = new List<AgentProgress>();
        var client = new Mock<ILlmClient>();
        client.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns<LlmRequest, CancellationToken>((r, _) => {
                requests.Add(r);
                return requests.Count == 1 ? Task.FromException<LlmResponse>(new LlmException(kind, "temporary", status,
                    inner: kind == LlmErrorKind.Unreachable ? new HttpRequestException(HttpRequestError.ConnectionError, "reset", new SocketException((int)SocketError.ConnectionReset)) : null))
                    : Task.FromResult(new LlmResponse("Verified", "test"));
            });
        var budget = new AgentBudget(5, 5, 0);
        budget.ReserveFinalReport();
        var failures = 0;
        var reads = 0;
        var adapter = new LlmChatClientAdapter(client.Object, budget, new(),
            (e, _) => { events.Add(e); return Task.CompletedTask; }, "member", _ => failures++);
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create(() => { reads++; return "original"; }, "read")] };
        var result = await adapter.GetResponseAsync([
            new(ChatRole.User, "Diagnose"),
            new(ChatRole.Assistant, [new FunctionCallContent("read1", "read", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("read1", "Already collected evidence")])], options, TestContext.Current.CancellationToken);
        Assert.Equal("Verified", result.Text);
        Assert.Equal(2, requests.Count);
        Assert.Same(requests[0], requests[1]);
        Assert.Equal("Already collected evidence", requests[1].Conversation!.Last().Content);
        Assert.Equal(0, reads);
        Assert.Equal(0, failures);
        Assert.Equal(2, budget.ModelCalls);
        Assert.Single(events, e => e.Kind == "model_retrying");
        Assert.DoesNotContain(events, e => e.Kind == "model_failed");
    }

    [Theory]
    [InlineData(LlmErrorKind.Unauthorized, 401)]
    [InlineData(LlmErrorKind.UpstreamError, 400)]
    [InlineData(LlmErrorKind.UpstreamError, 501)]
    [InlineData(LlmErrorKind.Unreachable, null)]
    [InlineData(LlmErrorKind.MalformedResponse, null)]
    public async Task PermanentFailureDoesNotRetry(LlmErrorKind kind, int? status)
    {
        var client = new Mock<ILlmClient>();
        client.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmException(kind, "permanent", status));
        var budget = new AgentBudget(10, 10, 0);
        var adapter = new LlmChatClientAdapter(client.Object, budget, new(), (_, _) => Task.CompletedTask, "member");
        await Assert.ThrowsAsync<LlmException>(() => adapter.GetResponseAsync([new(ChatRole.User, "Task")], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, budget.ModelCalls);
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError, SocketError.ConnectionReset, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.ConnectionAborted, true)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.NetworkReset, true)]
    [InlineData(HttpRequestError.ResponseEnded, SocketError.Success, true)]
    [InlineData(HttpRequestError.SecureConnectionError, SocketError.ConnectionReset, false)]
    [InlineData(HttpRequestError.NameResolutionError, SocketError.HostNotFound, false)]
    [InlineData(HttpRequestError.ConnectionError, SocketError.ConnectionRefused, false)]
    public async Task OnlyIdentifiedConnectionInterruptionsRetry(HttpRequestError stage, SocketError socket, bool retry)
    {
        var calls = 0;
        var client = new Mock<ILlmClient>();
        Exception? cause = socket == SocketError.Success ? null : new SocketException((int)socket);
        if (stage == HttpRequestError.SecureConnectionError)
            cause = new AuthenticationException("Certificate or handshake rejected", cause);
        var failure = new LlmException(LlmErrorKind.Unreachable, "Transport failed",
            inner: new HttpRequestException(stage, "Transport failed", cause));
        client.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns<LlmRequest, CancellationToken>((_, _) => ++calls == 1
                ? Task.FromException<LlmResponse>(failure) : Task.FromResult(new LlmResponse("Recovered", "test")));
        var budget = new AgentBudget(5, 5, 0);
        var adapter = new LlmChatClientAdapter(client.Object, budget, new(), (_, _) => Task.CompletedTask, "member");
        var task = adapter.GetResponseAsync([new(ChatRole.User, "Task")], cancellationToken: TestContext.Current.CancellationToken);
        if (retry) Assert.Equal("Recovered", (await task).Text);
        else await Assert.ThrowsAsync<LlmException>(() => task);
        Assert.Equal(retry ? 2 : 1, calls);
        Assert.Equal(calls, budget.ModelCalls);
    }

    [Fact]
    public async Task RetryCannotConsumeReservedFinalReportCall()
    {
        var client = new Mock<ILlmClient>();
        client.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmException(LlmErrorKind.Timeout, "timeout"));
        var budget = new AgentBudget(2, 10, 0);
        budget.ReserveFinalReport();
        var adapter = new LlmChatClientAdapter(client.Object, budget, new(), (_, _) => Task.CompletedTask, "member");
        await Assert.ThrowsAsync<LlmException>(() => adapter.GetResponseAsync([new(ChatRole.User, "Task")], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, budget.ModelCalls);
        budget.BeginFinalReport();
        Assert.Equal(1, budget.RemainingModelCalls);
    }

    [Fact]
    public async Task WorkingSummaryRetryLeavesRoomForTheAgentAnswer()
    {
        var client = new Mock<ILlmClient>();
        client.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmException(LlmErrorKind.Timeout, "timeout"));
        var budget = new AgentBudget(3, 10, 0);
        budget.ReserveFinalReport();
        var adapter = new LlmChatClientAdapter(client.Object, budget, new(), (_, _) => Task.CompletedTask, "member");
        await Assert.ThrowsAsync<LlmException>(() => adapter.CompleteWorkingAsync(new("Summarize", "Evidence"), "context_summary", TestContext.Current.CancellationToken));
        Assert.Equal(1, budget.ModelCalls);
        Assert.Equal(1, budget.RemainingModelCalls);
    }

    [Fact]
    public async Task CancellationDuringRetryStopsBeforeSecondTransportCall()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new Mock<ILlmClient>();
        client.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmException(LlmErrorKind.Timeout, "timeout"));
        var budget = new AgentBudget(10, 10, 0);
        var adapter = new LlmChatClientAdapter(client.Object, budget, new(), (e, _) => {
            if (e.Kind == "model_retrying") stop.Cancel();
            return Task.CompletedTask;
        }, "member");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetResponseAsync([new(ChatRole.User, "Task")], cancellationToken: stop.Token));
        Assert.Equal(1, budget.ModelCalls);
    }

    [Theory]
    [InlineData(false, 1_000_000, false)]
    [InlineData(true, 1_000_000, false)]
    [InlineData(false, 512, false)]
    [InlineData(false, 1_000_000, true)]
    [InlineData(true, 512, true)]
    public async Task StalledReviewerRetriesOnceWithoutRepeatingRead(bool stallBody, int profileTokens, bool recover)
    {
        var events = new List<AgentProgress>();
        using var handler = new StalledReviewerHandler(stallBody, recover);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(http);
        var options = new Mock<IOptionsMonitor<LlmOptions>>();
        options.SetupGet(x => x.CurrentValue).Returns(new LlmOptions { Enabled = true, ActiveProfileId = "test",
            Profiles = new() { ["test"] = new() { BaseUrl = "https://example.test/v1/responses", Model = "test", MaxTokens = profileTokens, TimeoutSeconds = 3600 } } });
        var runtime = new AgentRuntime(new LlmClientFactory(httpFactory.Object, options.Object, NullLoggerFactory.Instance), options.Object);
        var config = new AgentActivityConfiguration { Task = "Read evidence and review", Members = [
            new() { Id = "lead", Role = "Lead", IsSupervisor = true },
            new() { Id = "review", Role = "Quality", IsReviewer = true } ] };
        var reads = 0;
        var tools = new Dictionary<string, IReadOnlyList<AgentTool>> { ["lead"] = [], ["review"] = [
            new("read", "Read evidence", JsonSerializer.SerializeToElement(new { type = "object" }), (_, _) => { reads++; return Task.FromResult("Evidence"); })] };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        Task<AgentRunResult> Run() => runtime.RunAsync(config, true, tools, new(20, 40, 10),
            new() { ModelCallTimeoutSeconds = 1 }, (e, _) => { events.Add(e); return Task.CompletedTask; }, s => s, deadline.Token);
        if (recover)
        {
            var result = await Run();
            Assert.Equal("completed", result.Outcome);
            Assert.Equal(6, handler.Calls);
            Assert.DoesNotContain(events, e => e.Kind == "model_failed");
        }
        else
        {
            var error = await Assert.ThrowsAsync<LlmException>(Run);
            Assert.Equal(LlmErrorKind.Timeout, error.Kind);
            Assert.Contains("review", error.Message);
            Assert.Equal(4, handler.Calls);
            Assert.Contains(events, e => e.Kind == "model_failed" && e.MemberId == "review");
            Assert.DoesNotContain(events, e => e.Kind == "member_completed");
        }
        Assert.False(deadline.IsCancellationRequested);
        Assert.True(handler.Cancelled);
        Assert.Equal(1, reads);
        Assert.Single(events, e => e.Kind == "model_retrying" && e.MemberId == "review");
        Assert.All(handler.OutputLimits, limit => Assert.Equal(Math.Min(profileTokens, 250_000), limit));
    }

    private sealed class StalledReviewerHandler(bool stallBody, bool recover) : HttpMessageHandler
    {
        public int Calls;
        public bool Cancelled;
        public List<int> OutputLimits { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            OutputLimits.Add(body.RootElement.GetProperty("max_output_tokens").GetInt32());
            Calls++;
            if (Calls >= 3 && (!recover || Calls == 3))
            {
                if (stallBody) return new(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream(() => Cancelled = true)) };
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            if (Calls >= 4)
            {
                var text = Calls == 4 ? "{\"status\":\"completed\",\"content\":\"Evidence verified\",\"verdict\":\"approved\",\"openChecks\":[]}"
                    : Calls == 5 ? "Evidence verified"
                    : "{\"outcome\":\"completed\",\"reason\":\"Evidence verified\",\"report\":\"Evidence verified\",\"coverage\":[{\"requirement\":\"Read and review\",\"status\":\"fulfilled\",\"basis\":\"Original read and review\"}]}";
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                    model = "test", status = "completed", output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } } }
                }), Encoding.UTF8, "application/json") };
            }
            var name = Calls == 1 ? "delegate" : "read";
            var arguments = Calls == 1 ? "{\"assignments\":[{\"memberId\":\"review\",\"task\":\"Check evidence\",\"reason\":\"Verify the findings\"}]}" : "{}";
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                model = "test", status = "completed", output = new[] { new { type = "function_call", call_id = "call" + Calls, name, arguments } }
            }), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StalledStream(Action cancelled) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled(); throw; }
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
