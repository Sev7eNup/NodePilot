using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace NodePilot.LoadTests;

/// <summary>
/// Connects to /hubs/execution, joins groups for a sampled set of executions, and records
/// how long after ExecuteAsync returned each terminal ExecutionStatusChanged event arrives.
/// Purely observational — failures here don't affect test outcomes.
/// </summary>
public class SignalRObserver : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HubConnection _connection;
    private readonly ConcurrentDictionary<Guid, DateTime> _triggeredAt = new();
    private readonly ConcurrentBag<double> _broadcastLatencyMs = new();
    private readonly double _sampleRate;

    public SignalRObserver(string apiBaseUrl, string token, double sampleRate)
    {
        _sampleRate = sampleRate;
        var hubUrl = apiBaseUrl.TrimEnd('/') + "/hubs/execution";
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, o => o.AccessTokenProvider = () => Task.FromResult<string?>(token))
            .WithAutomaticReconnect()
            .Build();

        _connection.On<ExecutionStatusEventDto>("ExecutionStatusChanged", RecordIfTerminal);
        _connection.On<LiveEventsBatchDto>("LiveEventsBatch", batch =>
        {
            foreach (var item in batch.Events ?? [])
            {
                if (item.Type != "ExecutionStatusChanged"
                    || item.Event.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                {
                    continue;
                }

                if (item.Event.Deserialize<ExecutionStatusEventDto>(JsonOptions) is { } evt)
                    RecordIfTerminal(evt);
            }
        });
    }

    public async Task StartAsync(CancellationToken ct) => await _connection.StartAsync(ct);

    public async Task TrackAsync(Guid executionId, DateTime triggeredAt, CancellationToken ct)
    {
        if (Random.Shared.NextDouble() >= _sampleRate) return;
        _triggeredAt[executionId] = triggeredAt;
        try { await _connection.InvokeAsync("JoinExecution", executionId.ToString(), ct); }
        catch { /* observer is best-effort */ }
    }

    public SignalRStats Snapshot()
    {
        var values = _broadcastLatencyMs.ToArray();
        Array.Sort(values);
        if (values.Length == 0)
            return new SignalRStats(0, 0, 0, 0);
        double p(double q)
        {
            var i = (int)Math.Clamp(Math.Floor(q * values.Length), 0, values.Length - 1);
            return values[i];
        }
        return new SignalRStats(values.Length, p(0.5), p(0.95), p(0.99));
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private void RecordIfTerminal(ExecutionStatusEventDto evt)
    {
        if (evt.Status is "Succeeded" or "Failed" or "Cancelled"
            && _triggeredAt.TryRemove(evt.ExecutionId, out var triggered))
        {
            var latency = (DateTime.UtcNow - triggered).TotalMilliseconds;
            _broadcastLatencyMs.Add(latency);
        }
    }

    private sealed record ExecutionStatusEventDto(Guid ExecutionId, Guid WorkflowId, string Status, string? ErrorMessage, DateTime? CompletedAt, string? TraceId);
    private sealed record LiveEventsBatchDto(LiveEventBatchItemDto[]? Events);
    private sealed record LiveEventBatchItemDto(string Type, JsonElement Event);
}

public record SignalRStats(int Samples, double P50Ms, double P95Ms, double P99Ms);
