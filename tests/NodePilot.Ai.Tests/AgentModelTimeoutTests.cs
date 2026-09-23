using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Ai.Agents;
using NodePilot.Core.Agents;
using Xunit;

namespace NodePilot.Ai.Tests;

public sealed class AgentModelTimeoutTests
{
    [Theory]
    [InlineData(false, 1_000_000)]
    [InlineData(true, 1_000_000)]
    [InlineData(false, 512)]
    public async Task StalledReviewerAfterToolResultsFailsWholeTeamWithoutRetry(bool stallBody, int profileTokens)
    {
        var events = new List<AgentProgress>();
        using var handler = new StalledReviewerHandler(stallBody);
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
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<LlmException>(() => runtime.RunAsync(config, true, tools, new(20, 40, 10),
            new() { ModelCallTimeoutSeconds = 1 }, (e, _) => { events.Add(e); return Task.CompletedTask; }, s => s, deadline.Token));
        Assert.Equal(LlmErrorKind.Timeout, error.Kind);
        Assert.Contains("review", error.Message);
        Assert.False(deadline.IsCancellationRequested);
        Assert.True(handler.Cancelled);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(1, reads);
        Assert.Contains(events, e => e.Kind == "model_failed" && e.MemberId == "review");
        Assert.DoesNotContain(events, e => e.Kind == "member_completed");
        Assert.All(handler.OutputLimits, limit => Assert.Equal(Math.Min(profileTokens, 250_000), limit));
    }

    private sealed class StalledReviewerHandler(bool stallBody) : HttpMessageHandler
    {
        public int Calls;
        public bool Cancelled;
        public List<int> OutputLimits { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            OutputLimits.Add(body.RootElement.GetProperty("max_output_tokens").GetInt32());
            Calls++;
            if (Calls == 3)
            {
                if (stallBody) return new(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream(() => Cancelled = true)) };
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            var name = Calls == 1 ? "delegate" : "read";
            var arguments = Calls == 1 ? "{\"memberId\":\"review\",\"task\":\"Check evidence\",\"reason\":\"Verify the findings\"}" : "{}";
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
