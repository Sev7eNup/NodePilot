using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Data;

namespace NodePilot.Api.Services;

public sealed record MachineStepStats(
    IReadOnlyDictionary<Guid, (int Total, int Failed)> Recent,
    IReadOnlyDictionary<Guid, int> Active);

/// <summary>Shares one short-lived step aggregation across machine list and detail requests.</summary>
public sealed class MachineStepStatsCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CancellationToken _stoppingToken;
    private readonly TimeProvider _clock;
    private readonly Func<NodePilotDbContext, DateTime, CancellationToken, Task<MachineStepStats>> _read;
    private readonly object _gate = new();
    private MachineStepStats? _cached;
    private DateTimeOffset _expiresAt;
    private Lazy<Task<MachineStepStats>>? _inFlight;

    public MachineStepStatsCache(IServiceScopeFactory scopeFactory, IHostApplicationLifetime lifetime)
        : this(scopeFactory, lifetime.ApplicationStopping, TimeProvider.System, ReadAsync) { }

    internal MachineStepStatsCache(
        IServiceScopeFactory scopeFactory, CancellationToken stoppingToken, TimeProvider clock,
        Func<NodePilotDbContext, DateTime, CancellationToken, Task<MachineStepStats>> read)
    {
        _scopeFactory = scopeFactory;
        _stoppingToken = stoppingToken;
        _clock = clock;
        _read = read;
    }

    public Task<MachineStepStats> GetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Lazy<Task<MachineStepStats>> pending;
        lock (_gate)
        {
            if (_cached is not null && _expiresAt > _clock.GetUtcNow())
                return Task.FromResult(_cached);
            pending = _inFlight ??= new Lazy<Task<MachineStepStats>>(
                RefreshAsync, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        // Request cancellation only abandons this caller's wait, never another caller's work.
        return pending.Value.WaitAsync(ct);
    }

    private async Task<MachineStepStats> RefreshAsync()
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
            var result = await _read(db, _clock.GetUtcNow().UtcDateTime, _stoppingToken)
                .ConfigureAwait(false);
            lock (_gate)
            {
                _cached = result;
                _expiresAt = _clock.GetUtcNow() + Ttl;
            }
            return result;
        }
        finally
        {
            lock (_gate) _inFlight = null;
        }
    }

    private static async Task<MachineStepStats> ReadAsync(
        NodePilotDbContext db, DateTime now, CancellationToken ct)
    {
        var since = now.AddDays(-7);
        var recentRows = await db.StepExecutions.AsNoTracking()
            .Where(s => s.StartedAt >= since && s.TargetMachine != null)
            .GroupBy(s => s.TargetMachine!)
            .Select(g => new
            {
                Target = g.Key,
                Total = g.Count(),
                Failed = g.Count(s => s.Status == ExecutionStatus.Failed),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var recent = new Dictionary<Guid, (int Total, int Failed)>();
        foreach (var row in recentRows)
            if (Guid.TryParse(row.Target, out var id))
                recent[id] = (row.Total, row.Failed);

        var activeRows = await db.StepExecutions.AsNoTracking()
            .Where(s => s.Status == ExecutionStatus.Running && s.TargetMachine != null)
            .GroupBy(s => s.TargetMachine!)
            .Select(g => new { Target = g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        var active = new Dictionary<Guid, int>();
        foreach (var row in activeRows)
            if (Guid.TryParse(row.Target, out var id))
                active[id] = row.Count;

        return new MachineStepStats(recent, active);
    }
}
