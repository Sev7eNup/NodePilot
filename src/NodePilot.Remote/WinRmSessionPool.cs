using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Remote;

/// <summary>
/// Pooling layer in front of <see cref="WinRmSessionFactory"/>. Once a WinRM runspace has been
/// opened, it is not closed after use — it is parked under a pool key (machine + credential +
/// transport config) and handed to the next step that targets the same host. Reusing it avoids
/// redoing the auth handshake and runspace setup on every step, which is by far the most
/// expensive part of a remote step (up to several hundred ms on slow networks or with Kerberos).
///
/// Registered as a singleton — the pool has to outlive individual DI scopes so that two
/// consecutive steps of the same execution can share a session. Because
/// <see cref="WinRmSessionFactory"/> depends on a scoped <see cref="ICredentialStore"/> (backed by
/// a DbContext), the pool creates its own DI scope for every fresh connection and disposes it
/// again as soon as the runspace is open.
///
/// Concurrency: a single WinRM runspace is not thread-safe for parallel invokes. Sessions are
/// therefore always handed out as an exclusive "lease" — two parallel steps against the same
/// machine get two separate sessions. The pool only ever buffers *idle* sessions, never ones
/// currently on loan.
///
/// Per-target throttling: WinRM servers cap the number of open shells per user (default
/// MaxShellsPerUser = 30 on Server 2012+, 5 on older builds, often hardened to 10-15 via Group
/// Policy). Without a throttle, 50 parallel fan-out steps against the same host would issue 50
/// simultaneous Open() calls — the WinRM server rejects the excess with Error 5 and the workflow
/// collapses into a wave of retries (a thundering herd). We therefore gate per pool key on
/// <c>Remote:Pool:MaxConcurrentPerMachine</c> (default 5, conservatively below the lowest common
/// WinRM quota). The gate is acquired in <see cref="CreateSessionAsync"/> and released in
/// <see cref="PooledWinRmSession.DisposeAsync"/> — regardless of whether the session goes back
/// into the pool or is actually closed.
///
/// Security: the pool key includes the credential ID. Two calls against the same host with
/// different credentials are guaranteed to get different sessions — reuse can never mix up
/// credentials or lead to privilege escalation.
/// </summary>
public sealed class WinRmSessionPool : IRemoteSessionFactory, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WinRmSessionPool>? _logger;
    private readonly TimeSpan _idleTtl;
    private readonly int _maxIdlePerKey;
    private readonly int _maxConcurrentPerMachine;
    private readonly bool _enabled;
    private readonly ConcurrentDictionary<PoolKey, ConcurrentQueue<PoolEntry>> _idle = new();
    private readonly ConcurrentDictionary<PoolKey, SemaphoreSlim> _machineGates = new();
    private readonly Timer? _sweeper;
    private int _disposed;

    public WinRmSessionPool(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<WinRmSessionPool>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        // Opt-out (default ON) — pooling is a pure optimization with no semantic change.
        // Deployments that need every step to run on a guaranteed-fresh session (e.g. because a
        // target app expects a new login per runspace) set Remote:Pool:Enabled=false. Note: this
        // also disables the per-target throttle below — if you need both, leave pooling on.
        _enabled = !string.Equals(configuration["Remote:Pool:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
        _idleTtl = TimeSpan.FromSeconds(configuration.GetValue<int?>("Remote:Pool:IdleTtlSeconds") ?? 120);
        _maxConcurrentPerMachine = Math.Max(1, configuration.GetValue<int?>("Remote:Pool:MaxConcurrentPerMachine") ?? 5);
        // The idle cap must be at least as large as the concurrency cap — otherwise a burst
        // (all N sessions being returned at once) immediately disposes N - MaxIdlePerKey of
        // them, forcing the next burst to reconnect from scratch. Defaults to the concurrency cap.
        var configuredIdle = configuration.GetValue<int?>("Remote:Pool:MaxIdlePerKey") ?? _maxConcurrentPerMachine;
        _maxIdlePerKey = Math.Max(_maxConcurrentPerMachine, configuredIdle);

        if (_enabled)
        {
            // Sweep interval is capped at 1/4 of the TTL, so expired entries don't sit around
            // unnecessarily long. Minimum 15s — shorter ticks aren't worth the overhead.
            var sweepInterval = TimeSpan.FromSeconds(Math.Max(15, _idleTtl.TotalSeconds / 4));
            _sweeper = new Timer(_ => Sweep(), null, sweepInterval, sweepInterval);
        }
    }

    public async Task<IRemoteSession> CreateSessionAsync(ManagedMachine machine, Credential? credential, CancellationToken ct)
    {
        if (!_enabled)
            return await CreateFreshAsync(machine, credential, ct);

        var key = BuildKey(machine, credential);
        var gate = _machineGates.GetOrAdd(key, _ => new SemaphoreSlim(_maxConcurrentPerMachine, _maxConcurrentPerMachine));

        // Per-machine throttle: queue for a slot first, only then touch the pool/open a new
        // session. If the caller cancels while waiting, WaitAsync throws OperationCanceledException
        // and nothing leaks — the semaphore slot was never acquired in that case.
        await gate.WaitAsync(ct);

        try
        {
            if (_idle.TryGetValue(key, out var queue))
            {
                while (queue.TryDequeue(out var entry))
                {
                    if (entry.Inner.IsAlive && (DateTime.UtcNow - entry.ReturnedAt) <= _idleTtl)
                    {
                        // Reuse path: SessionsActive was already incremented when the runspace
                        // was first opened and is only decremented on a real close — a session
                        // sitting idle in the pool still counts as "exists", not as an "active
                        // lease". No metric change on checkout.
                        return new PooledWinRmSession(entry.Inner, this, key);
                    }
                    // Stale / dead — discard and keep looking.
                    await SafeDisposeAsync(entry.Inner);
                }
            }

            var fresh = await CreateFreshAsync(machine, credential, ct);
            return new PooledWinRmSession(fresh, this, key);
        }
        catch
        {
            // Open failed, or disposing a stale session threw — release the slot regardless,
            // otherwise the quota counter stays permanently off and blocks later callers.
            gate.Release();
            throw;
        }
    }

    private async Task<WinRmSession> CreateFreshAsync(ManagedMachine machine, Credential? credential, CancellationToken ct)
    {
        // Every fresh connection gets its own DI scope — WinRmSessionFactory depends on a
        // scoped ICredentialStore (DbContext). The scope is torn down again as soon as the
        // runspace is open; the resulting session object is self-contained afterwards.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredService<WinRmSessionFactory>();
        var session = await inner.CreateSessionAsync(machine, credential, ct);
        // Pool handed out (or wrapper about to hand out) a WinRmSession instance.
        // The inner factory returns WinRmSession specifically; cast is safe.
        return (WinRmSession)session;
    }

    internal void Return(PoolKey key, WinRmSession session)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || !_enabled || !session.IsAlive)
            {
                _ = SafeDisposeAsync(session);
                return;
            }

            var queue = _idle.GetOrAdd(key, _ => new ConcurrentQueue<PoolEntry>());
            if (queue.Count >= _maxIdlePerKey)
            {
                // Pool is full for this key — dispose for real instead of keeping it, otherwise
                // the pool would grow without bound across many machines/credentials.
                _ = SafeDisposeAsync(session);
                return;
            }
            queue.Enqueue(new PoolEntry(session, DateTime.UtcNow));
            // The PooledWinRmSession wrapper has already decremented SessionsActive.
        }
        finally
        {
            // Always release the per-machine slot, even if the session ended up being disposed.
            // The slot represents an "active lease", which ends on Return regardless of whether
            // the session goes back into the pool or gets closed.
            if (_machineGates.TryGetValue(key, out var gate))
                gate.Release();
        }
    }

    private void Sweep()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var cutoff = DateTime.UtcNow - _idleTtl;
        foreach (var (_, queue) in _idle)
        {
            // We only look at the head of the queue — entries are time-ordered (FIFO, with equal
            // return timestamps possible in quick succession), so the first still-fresh entry
            // stops the sweep for this queue.
            while (queue.TryPeek(out var head) && head.ReturnedAt < cutoff)
            {
                if (queue.TryDequeue(out var evicted))
                    _ = SafeDisposeAsync(evicted.Inner);
                else
                    break;
            }
        }
    }

    private static ValueTask SafeDisposeAsync(WinRmSession session)
    {
        try { return session.DisposeUnpooledAsync(); }
        catch { return ValueTask.CompletedTask; }
    }

    private static PoolKey BuildKey(ManagedMachine machine, Credential? credential)
        => new(machine.Id, credential?.Id ?? Guid.Empty, machine.Hostname ?? string.Empty, machine.WinRmPort, machine.UseSsl);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_sweeper is not null) await _sweeper.DisposeAsync();
        foreach (var (_, queue) in _idle)
        {
            while (queue.TryDequeue(out var entry))
                await SafeDisposeAsync(entry.Inner);
        }
        _idle.Clear();
        foreach (var (_, gate) in _machineGates)
            gate.Dispose();
        _machineGates.Clear();
    }

    internal readonly record struct PoolKey(Guid MachineId, Guid CredentialId, string Hostname, int Port, bool UseSsl);
    private sealed record PoolEntry(WinRmSession Inner, DateTime ReturnedAt);
}

/// <summary>
/// IRemoteSession wrapper that returns the underlying <see cref="WinRmSession"/> to the pool on
/// Dispose instead of closing it. Script execution is passed straight through unchanged.
/// </summary>
internal sealed class PooledWinRmSession : IRemoteSession
{
    private readonly WinRmSession _inner;
    private readonly WinRmSessionPool _pool;
    private readonly WinRmSessionPool.PoolKey _key;
    private int _disposed;

    public PooledWinRmSession(WinRmSession inner, WinRmSessionPool pool, WinRmSessionPool.PoolKey key)
    {
        _inner = inner;
        _pool = pool;
        _key = key;
    }

    public Task<RemoteExecutionResult> ExecuteScriptAsync(string script, int? timeoutSeconds = null, CancellationToken ct = default)
        => _inner.ExecuteScriptAsync(script, timeoutSeconds, ct);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        // No metric change here — SessionsActive reflects "runspace exists" (incremented in
        // WinRmSessionFactory.CreateSessionAsync, decremented in WinRmSession.DisposeAsync).
        // Returning a lease just parks the session in the pool; the runspace stays open.
        _pool.Return(_key, _inner);
        return ValueTask.CompletedTask;
    }
}
