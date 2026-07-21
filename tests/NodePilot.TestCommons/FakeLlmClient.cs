using System.Runtime.CompilerServices;
using NodePilot.Ai;

namespace NodePilot.TestCommons;

/// <summary>
/// Test stub for <see cref="ILlmClient"/>: an in-memory, programmable queue. Tests enqueue
/// responses/exceptions up front, and the service under test dequeues them in order.
/// Every CompleteAsync/StreamAsync call's request is recorded in <see cref="Calls"/> so a
/// test can assert on it afterwards (e.g. that JsonMode/Conversation was set correctly).
///
/// Lives in TestCommons because both NodePilot.Ai.Tests (service unit tests) and the AI
/// controller tests still in NodePilot.Api.Tests need the same stub.
/// </summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly Queue<Func<LlmRequest, Task<LlmResponse>>> _responses = new();
    private readonly Queue<Func<LlmRequest, IAsyncEnumerable<LlmStreamEvent>>> _streams = new();
    public List<LlmRequest> Calls { get; } = new();

    public FakeLlmClient EnqueueContent(string content, string model = "fake-model")
    {
        _responses.Enqueue(_ => Task.FromResult(new LlmResponse(content, model)));
        return this;
    }

    public FakeLlmClient EnqueueException(LlmException ex)
    {
        _responses.Enqueue(_ => Task.FromException<LlmResponse>(ex));
        return this;
    }

    /// <summary>Enqueues a streaming response: each string becomes a content delta, followed by a Done event carrying token usage.</summary>
    public FakeLlmClient EnqueueStream(params string[] chunks)
    {
        _streams.Enqueue(_ => ToStream(chunks));
        return this;
    }

    /// <summary>Enqueues a stream that throws before emitting its first delta (simulates a failure before streaming even starts).</summary>
    public FakeLlmClient EnqueueStreamException(LlmException ex)
    {
        _streams.Enqueue(_ => ThrowingStream(ex));
        return this;
    }

    /// <summary>Enqueues a streaming response that ends with <c>finish_reason=tool_calls</c> (for exercising the tool-calling loop).</summary>
    public FakeLlmClient EnqueueToolCallStream(IReadOnlyList<LlmToolCall> toolCalls, params string[] chunks)
    {
        _streams.Enqueue(_ => ToolCallStream(chunks, toolCalls, "tool_calls"));
        return this;
    }

    /// <summary>
    /// Like <see cref="EnqueueToolCallStream"/> but with a caller-chosen <paramref name="finishReason"/>.
    /// Pass <c>"stop"</c>/<c>null</c> to simulate a local endpoint (LM Studio, llama.cpp) that reports a
    /// non-canonical finish_reason while still emitting tool calls — the loop must execute them regardless.
    /// </summary>
    public FakeLlmClient EnqueueToolCallStreamWithFinish(IReadOnlyList<LlmToolCall> toolCalls, string? finishReason, params string[] chunks)
    {
        _streams.Enqueue(_ => ToolCallStream(chunks, toolCalls, finishReason));
        return this;
    }

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        Calls.Add(request);
        if (_responses.Count == 0)
            throw new InvalidOperationException(
                "FakeLlmClient was called but no response was enqueued. Use EnqueueContent / EnqueueException.");
        var next = _responses.Dequeue();
        return next(request);
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Calls.Add(request);
        if (_streams.Count == 0)
            throw new InvalidOperationException(
                "FakeLlmClient.StreamAsync was called but no stream was enqueued. Use EnqueueStream / EnqueueStreamException.");
        var next = _streams.Dequeue();
        await foreach (var evt in next(request).WithCancellation(ct))
            yield return evt;
    }

    private static async IAsyncEnumerable<LlmStreamEvent> ToStream(string[] chunks)
    {
        await Task.Yield();
        foreach (var c in chunks)
            yield return new LlmStreamEvent(c, Model: "fake-model");
        yield return new LlmStreamEvent(null, Done: true, Model: "fake-model", PromptTokens: 1, CompletionTokens: 1);
    }

    private static async IAsyncEnumerable<LlmStreamEvent> ToolCallStream(string[] chunks, IReadOnlyList<LlmToolCall> toolCalls, string? finishReason)
    {
        await Task.Yield();
        foreach (var c in chunks)
            yield return new LlmStreamEvent(c, Model: "fake-model");
        yield return new LlmStreamEvent(null, Done: true, Model: "fake-model", PromptTokens: 1, CompletionTokens: 1,
            ToolCalls: toolCalls, FinishReason: finishReason);
    }

    private static async IAsyncEnumerable<LlmStreamEvent> ThrowingStream(LlmException ex)
    {
        await Task.CompletedTask;
        if (ex is not null) throw ex;
        yield break;
    }
}
