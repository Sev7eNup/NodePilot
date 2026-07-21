using System.Diagnostics;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Remote;

public class NoOpRemoteOptions
{
    public int MinLatencyMs { get; set; } = 0;
    public int MaxLatencyMs { get; set; } = 0;
    public bool SimulateFailures { get; set; } = false;
    public double FailureRate { get; set; } = 0.0;
}

/// <summary>
/// Fake <see cref="IRemoteSessionFactory"/> that produces in-process "sessions" which never
/// touch WinRM. Intended for load tests against a real API host, where we want to stress
/// the engine/DB/SignalR path without booting Windows targets. Activated via
/// <c>Remote:Provider = "noop"</c> in configuration.
/// </summary>
public class NoOpSessionFactory : IRemoteSessionFactory
{
    private readonly NoOpRemoteOptions _options;

    public NoOpSessionFactory(NoOpRemoteOptions options)
    {
        _options = options;
    }

    public Task<IRemoteSession> CreateSessionAsync(ManagedMachine machine, Credential? credential, CancellationToken ct)
    {
        RemoteMetrics.SessionsOpened.Add(1,
            new KeyValuePair<string, object?>("result", "ok"),
            new KeyValuePair<string, object?>("auth", "noop"));
        RemoteMetrics.SessionsActive.Add(1);
        return Task.FromResult<IRemoteSession>(new NoOpSession(_options));
    }
}

internal sealed class NoOpSession : IRemoteSession
{
    private readonly NoOpRemoteOptions _options;

    public NoOpSession(NoOpRemoteOptions options)
    {
        _options = options;
    }

    public async Task<RemoteExecutionResult> ExecuteScriptAsync(string script, int? timeoutSeconds = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var delayMs = _options.MinLatencyMs;
        if (_options.MaxLatencyMs > _options.MinLatencyMs)
            delayMs = Random.Shared.Next(_options.MinLatencyMs, _options.MaxLatencyMs + 1);

        if (delayMs > 0)
            await Task.Delay(delayMs, ct);

        var fail = _options.SimulateFailures
                   && _options.FailureRate > 0
                   && Random.Shared.NextDouble() < _options.FailureRate;

        sw.Stop();

        RemoteMetrics.ScriptDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("result", fail ? "fail" : "ok"));

        return new RemoteExecutionResult
        {
            Success = !fail,
            Output = fail ? string.Empty : "noop",
            ErrorOutput = fail ? "Simulated failure (NoOpSession)" : string.Empty,
            Duration = sw.Elapsed
        };
    }

    public ValueTask DisposeAsync()
    {
        RemoteMetrics.SessionsActive.Add(-1);
        return ValueTask.CompletedTask;
    }
}
