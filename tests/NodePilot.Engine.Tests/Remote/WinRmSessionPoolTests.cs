using System.Collections.Concurrent;
using System.Management.Automation.Runspaces;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Remote;
using Xunit;

namespace NodePilot.Engine.Tests.Remote;

/// <summary>
/// Behavior of the <see cref="WinRmSessionPool"/> under concurrent calls. We bypass the real
/// WinRM stack by swapping in a test subclass of <see cref="WinRmSessionFactory"/> that returns
/// a local runspace instead — the pool still sees real <see cref="WinRmSession"/> instances and
/// exercises its lease/throttle logic without any network traffic.
/// </summary>
public class WinRmSessionPoolTests
{
    /// <summary>
    /// Test factory: creates a local runspace on each call and counts how many times it was
    /// invoked — the pool still gets real <see cref="WinRmSession"/> instances without needing
    /// an actual WinRM server. A local runspace stands in for a WinRM connection with zero
    /// network use.
    /// </summary>
    private sealed class StubFactory : WinRmSessionFactory
    {
        public int CreateCount;

        public StubFactory() : base(Mock.Of<ICredentialStore>()) { }

        public override Task<IRemoteSession> CreateSessionAsync(ManagedMachine machine, Credential? credential, CancellationToken ct)
        {
            Interlocked.Increment(ref CreateCount);
            var rs = RunspaceFactory.CreateRunspace();
            rs.Open();
            return Task.FromResult<IRemoteSession>(new WinRmSession(rs, machine.Hostname));
        }
    }

    private static (WinRmSessionPool pool, StubFactory factory) BuildPool(IConfiguration? config = null)
    {
        var factory = new StubFactory();
        var services = new ServiceCollection();
        services.AddSingleton<WinRmSessionFactory>(factory);
        var sp = services.BuildServiceProvider();
        var pool = new WinRmSessionPool(
            sp.GetRequiredService<IServiceScopeFactory>(),
            config ?? new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<WinRmSessionPool>.Instance);
        return (pool, factory);
    }

    private static ManagedMachine Machine(string hostname = "h.example.net") => new()
    {
        Id = Guid.NewGuid(),
        Name = "T",
        Hostname = hostname,
        WinRmPort = 5985,
        UseSsl = false,
    };

    [Fact]
    public async Task CreateSessionAsync_PerTargetSemaphore_BlocksBeyondConfiguredLimit()
    {
        // The core fix for the enterprise scalability gap: a 6th concurrent lease against the
        // same host may only start once a previous caller has disposed its session. Without this
        // throttle, all 6 connects would fire at the WinRM server simultaneously and blow past
        // its connection quota.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Remote:Pool:MaxConcurrentPerMachine"] = "5",
        }).Build();
        var (pool, factory) = BuildPool(config);
        await using var _ = pool;

        var machine = Machine();
        var leases = new List<IRemoteSession>();
        for (var i = 0; i < 5; i++)
            leases.Add(await pool.CreateSessionAsync(machine, null, CancellationToken.None));

        factory.CreateCount.Should().Be(5, "die ersten fünf Leases dürfen sofort durch");

        // The 6th lease must block until an existing lease is released.
        var sixthTask = pool.CreateSessionAsync(machine, null, CancellationToken.None);
        await Task.Delay(100);
        sixthTask.IsCompleted.Should().BeFalse("der 6. Caller muss durch das Per-Machine-Semaphor blockiert sein");

        await leases[0].DisposeAsync();
        var sixth = await sixthTask.WaitAsync(TimeSpan.FromSeconds(2));
        sixth.Should().NotBeNull();

        foreach (var s in leases.Skip(1))
            await s.DisposeAsync();
        await sixth.DisposeAsync();
    }

    [Fact]
    public async Task CreateSessionAsync_PerTargetSemaphore_DifferentMachinesShareNoBudget()
    {
        // Sanity check: the throttle is scoped *per* pool key (machine + credential +
        // transport). Two different hosts must each be able to use their own full budget.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Remote:Pool:MaxConcurrentPerMachine"] = "2",
        }).Build();
        var (pool, factory) = BuildPool(config);
        await using var _ = pool;

        var leases = new List<IRemoteSession>();
        leases.Add(await pool.CreateSessionAsync(Machine("a.example.net"), null, CancellationToken.None));
        leases.Add(await pool.CreateSessionAsync(Machine("a.example.net"), null, CancellationToken.None));
        leases.Add(await pool.CreateSessionAsync(Machine("b.example.net"), null, CancellationToken.None));
        leases.Add(await pool.CreateSessionAsync(Machine("b.example.net"), null, CancellationToken.None));

        factory.CreateCount.Should().Be(4);
        foreach (var s in leases) await s.DisposeAsync();
    }

    [Fact]
    public async Task CreateSessionAsync_OpenFailsAfterGateAcquired_ReleasesGate()
    {
        // If Open() crashes (e.g. the WinRM server is unreachable), the per-machine slot must
        // not stay stuck — otherwise a single failed connect would permanently block a quota
        // slot and leave every subsequent caller hanging.
        var failingFactory = new Mock<WinRmSessionFactory>(MockBehavior.Loose, Mock.Of<ICredentialStore>());
        failingFactory.Setup(f => f.CreateSessionAsync(It.IsAny<ManagedMachine>(), It.IsAny<Credential?>(), It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new InvalidOperationException("connect refused"));

        var services = new ServiceCollection();
        services.AddSingleton(failingFactory.Object);
        var sp = services.BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Remote:Pool:MaxConcurrentPerMachine"] = "1",
        }).Build();
        await using var pool = new WinRmSessionPool(sp.GetRequiredService<IServiceScopeFactory>(), config, NullLogger<WinRmSessionPool>.Instance);

        var machine = Machine();

        // First attempt crashes.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pool.CreateSessionAsync(machine, null, CancellationToken.None));

        // Second attempt must NOT hang — the slot must have been released after the failure.
        var secondAttempt = pool.CreateSessionAsync(machine, null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => secondAttempt.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CreateSessionAsync_PoolDisabled_BypassesThrottle()
    {
        // Opt-out path: disabling the pool (Remote:Pool:Enabled=false) also disables the
        // throttle. This lets NoOp tests / load tests scale up unlimited parallel calls
        // without any quota bookkeeping getting in the way.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Remote:Pool:Enabled"] = "false",
            ["Remote:Pool:MaxConcurrentPerMachine"] = "1",
        }).Build();
        var (pool, factory) = BuildPool(config);
        await using var _ = pool;

        var machine = Machine();
        var leases = new List<IRemoteSession>();
        for (var i = 0; i < 10; i++)
            leases.Add(await pool.CreateSessionAsync(machine, null, CancellationToken.None));

        factory.CreateCount.Should().Be(10, "ohne Pool gibt es auch keinen Throttle — Caller bekommt direkten Factory-Zugriff");
        foreach (var s in leases) await s.DisposeAsync();
    }

    [Fact]
    public async Task Return_PooledSession_ReusedOnNextCheckout()
    {
        // Reuse path: after Dispose, the session lands in the idle pool and is handed back on
        // the next checkout without reopening. Without this cache the whole point of pooling
        // would be lost — the auth handshake is the most expensive part of a connection.
        var (pool, factory) = BuildPool();
        await using var _ = pool;

        var machine = Machine();
        var first = await pool.CreateSessionAsync(machine, null, CancellationToken.None);
        await first.DisposeAsync();
        var second = await pool.CreateSessionAsync(machine, null, CancellationToken.None);

        factory.CreateCount.Should().Be(1, "die zweite Anforderung muss aus dem Pool bedient werden");
        await second.DisposeAsync();
    }

    [Fact]
    public async Task CreateSessionAsync_CallerCancelDuringWait_DoesNotLeakSlot()
    {
        // If a caller is cancelled while still waiting on the per-machine gate, the slot must
        // not be consumed — otherwise the quota counter would drift over time and eventually
        // cause a total stall. WaitAsync(ct) propagates OperationCanceledException BEFORE the
        // try/catch in CreateSessionAsync gets a chance to run.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Remote:Pool:MaxConcurrentPerMachine"] = "1",
        }).Build();
        var (pool, factory) = BuildPool(config);
        await using var _ = pool;

        var machine = Machine();
        var blocking = await pool.CreateSessionAsync(machine, null, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pool.CreateSessionAsync(machine, null, cts.Token));

        // The original lease is still alive, so the slot stays occupied. After it's released,
        // the next caller MUST go through immediately — the cancelled caller must not have
        // accidentally consumed the counter.
        await blocking.DisposeAsync();
        var nextTask = pool.CreateSessionAsync(machine, null, CancellationToken.None);
        var next = await nextTask.WaitAsync(TimeSpan.FromSeconds(2));
        await next.DisposeAsync();
    }

    // A local loopback runspace produces a real WinRmSession instance without touching the network (see StubFactory).
    private static WinRmSession LiveSession(string host = "h.example.net")
    {
        var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        return new WinRmSession(rs, host);
    }

    private static WinRmSessionPool.PoolKey KeyFor(ManagedMachine m) =>
        new(m.Id, Guid.Empty, m.Hostname ?? string.Empty, m.WinRmPort, m.UseSsl);

    [Fact]
    public async Task Return_SurplusBeyondIdleCap_DisposesSurplusSession()
    {
        // Idle-cap guard: the pool must not grow without bound. Once the idle slot for a given
        // key is full, a returned session is actually closed instead of being cached.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Remote:Pool:MaxConcurrentPerMachine"] = "1", // ⇒ MaxIdlePerKey = max(1, default) = 1
        }).Build();
        var (pool, _) = BuildPool(config);
        await using var _p = pool;

        var machine = Machine();
        var key = KeyFor(machine);
        var s1 = LiveSession(machine.Hostname!);
        var s2 = LiveSession(machine.Hostname!);

        pool.Return(key, s1); // fits into the idle pool
        pool.Return(key, s2); // idle pool already at capacity ⇒ actually disposed

        s1.IsAlive.Should().BeTrue("die erste Session bleibt für Reuse im Idle-Pool");
        s2.IsAlive.Should().BeFalse("die überzählige Session wird geschlossen statt gepoolt");
    }

    [Fact]
    public async Task Return_DeadSession_IsDisposedNotPooled()
    {
        // A session that's already dead (poisoned/closed) must never end up in the idle pool —
        // otherwise the next checkout would hand a dead runspace to a step.
        var (pool, factory) = BuildPool();
        await using var _p = pool;

        var machine = Machine();
        var key = KeyFor(machine);
        var dead = LiveSession(machine.Hostname!);
        await dead.DisposeUnpooledAsync();
        dead.IsAlive.Should().BeFalse();

        pool.Return(key, dead); // not-alive short-circuit: dispose it, don't enqueue it, don't throw

        // The next checkout must open a fresh session (the dead one was not pooled).
        var next = await pool.CreateSessionAsync(machine, null, CancellationToken.None);
        factory.CreateCount.Should().Be(1);
        await next.DisposeAsync();
    }

    [Fact]
    public async Task Sweep_EvictsStaleIdleSessions()
    {
        // The TTL sweeper (timer-driven in production) closes idle sessions whose time in the
        // pool exceeds IdleTtl. Here we trigger it deterministically via the private Sweep()
        // entry point instead of waiting for the 15-second timer.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Remote:Pool:IdleTtlSeconds"] = "0", // jede Rücklage ist sofort abgelaufen
        }).Build();
        var (pool, _) = BuildPool(config);
        await using var _p = pool;

        var machine = Machine();
        var session = LiveSession(machine.Hostname!);
        pool.Return(KeyFor(machine), session);
        session.IsAlive.Should().BeTrue("frisch zurückgelegt, noch nicht gesweept");

        await Task.Delay(50); // make sure the cutoff is strictly after ReturnedAt

        var sweep = typeof(WinRmSessionPool).GetMethod("Sweep", BindingFlags.NonPublic | BindingFlags.Instance)!;
        sweep.Invoke(pool, null);

        session.IsAlive.Should().BeFalse("abgelaufene Idle-Session wird vom Sweeper geschlossen");
    }
}
