using FluentAssertions;
using NodePilot.Data;
using NodePilot.Scheduler;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Scheduler;

/// <summary>
/// SystemHealthWriter debounces successful writes per service to avoid hammering
/// the SQLite single-writer lock with sub-30 s heartbeats. These tests verify the
/// debounce skips back-to-back writes and persists the row on the first successful
/// write.
/// </summary>
public sealed class SystemHealthWriterTests : IDisposable
{
    public SystemHealthWriterTests()
    {
        // The debounce table is process-static; reset it so tests in any order see a clean slate.
        SystemHealthWriter.ResetDebounceForTests();
    }

    public void Dispose()
    {
        // Restore the production clock so this test class doesn't leak fakes into other tests.
        SystemHealthWriter.NowProvider = static () => DateTime.UtcNow;
        SystemHealthWriter.ResetDebounceForTests();
    }

    [Fact]
    public async Task BeatAsync_FirstCall_PersistsHeartbeatRow()
    {
        using var db = TestDbFactory.Create();

        // 60 s is above the 30 s debounce floor, so this stays a clean round-trip assertion
        // unaffected by the flooring covered separately below.
        await SystemHealthWriter.BeatAsync(db, "TestService-First", expectedIntervalSeconds: 60, status: "ok");

        var row = await db.SystemHealth.FindAsync("TestService-First");
        row.Should().NotBeNull();
        row!.Status.Should().Be("ok");
        row.ExpectedIntervalSeconds.Should().Be(60);
    }

    /// <summary>
    /// A service physically cannot beat more often than the 30 s debounce window, so an
    /// interval below it is floored. Without this, the dashboard's
    /// "age &gt; 3 × ExpectedIntervalSeconds" stale-check would flag a healthy 5 s-tick
    /// service (TriggerOrchestrator) as "stale" for half of every 30 s debounce cycle.
    /// </summary>
    [Fact]
    public async Task BeatAsync_IntervalBelowDebounce_IsFlooredToDebounceSeconds()
    {
        using var db = TestDbFactory.Create();

        await SystemHealthWriter.BeatAsync(db, "TestService-Floor", expectedIntervalSeconds: 5, status: "ok");

        var row = await db.SystemHealth.FindAsync("TestService-Floor");
        row!.ExpectedIntervalSeconds.Should().Be(30, "5 s is below the 30 s debounce floor");
    }

    [Fact]
    public async Task BeatAsync_IntervalAtOrAboveDebounce_IsPreserved()
    {
        using var db = TestDbFactory.Create();

        await SystemHealthWriter.BeatAsync(db, "TestService-NoFloor", expectedIntervalSeconds: 300, status: "ok");

        var row = await db.SystemHealth.FindAsync("TestService-NoFloor");
        row!.ExpectedIntervalSeconds.Should().Be(300, "intervals at or above the debounce floor are untouched");
    }

    [Fact]
    public async Task BeatAsync_SecondCallWithinDebounce_IsSkipped()
    {
        using var db = TestDbFactory.Create();

        await SystemHealthWriter.BeatAsync(db, "TestService-Debounce", 5, status: "first");
        var firstWriteAt = (await db.SystemHealth.FindAsync("TestService-Debounce"))!.LastHeartbeatAt;

        // Detach the entity so a hypothetical second write would actually be observable
        // through a fresh Find.
        db.ChangeTracker.Clear();

        await SystemHealthWriter.BeatAsync(db, "TestService-Debounce", 5, status: "second");

        var afterSecond = await db.SystemHealth.FindAsync("TestService-Debounce");
        afterSecond!.Status.Should().Be("first", "the second call must be debounced (no second SaveChanges)");
        afterSecond.LastHeartbeatAt.Should().Be(firstWriteAt);
    }

    [Fact]
    public async Task BeatAsync_DifferentServices_BothPersistWithinDebounceWindow()
    {
        using var db = TestDbFactory.Create();

        await SystemHealthWriter.BeatAsync(db, "TestService-A", 5, status: "a");
        await SystemHealthWriter.BeatAsync(db, "TestService-B", 5, status: "b");

        var rowA = await db.SystemHealth.FindAsync("TestService-A");
        var rowB = await db.SystemHealth.FindAsync("TestService-B");
        rowA.Should().NotBeNull();
        rowB.Should().NotBeNull();
        rowA!.Status.Should().Be("a");
        rowB!.Status.Should().Be("b");
    }

    /// <summary>
    /// After the debounce window elapses, the next call must write again. Without
    /// clock-injection this would require sleeping 30 s; with NowProvider the test runs
    /// instantly and verifies the actual elapsed-window reset, not just the skip case.
    /// </summary>
    [Fact]
    public async Task BeatAsync_AfterDebounceWindow_WritesAgain()
    {
        using var db = TestDbFactory.Create();
        var fakeNow = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        SystemHealthWriter.NowProvider = () => fakeNow;

        await SystemHealthWriter.BeatAsync(db, "TestService-Window", 5, status: "first");
        var firstAt = (await db.SystemHealth.FindAsync("TestService-Window"))!.LastHeartbeatAt;

        // Advance the clock past the 30 s debounce window.
        fakeNow = fakeNow.AddSeconds(31);
        db.ChangeTracker.Clear();

        await SystemHealthWriter.BeatAsync(db, "TestService-Window", 5, status: "second");

        var secondRow = await db.SystemHealth.FindAsync("TestService-Window");
        secondRow!.Status.Should().Be("second", "after the debounce window elapses, BeatAsync writes the new status");
        secondRow.LastHeartbeatAt.Should().BeAfter(firstAt);
    }

    /// <summary>
    /// A SaveChanges failure is swallowed (the catch-all exists to keep a transient
    /// DB blip from cascading into a service restart). The debounce slot is *still*
    /// reserved so we don't retry-storm against the broken DB inside the same window —
    /// the next attempt happens 30 s later, exactly like the healthy path.
    /// </summary>
    [Fact]
    public async Task BeatAsync_SaveChangesThrows_ExceptionIsSwallowedAndDebounceSlotIsReserved()
    {
        var fakeNow = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        SystemHealthWriter.NowProvider = () => fakeNow;

        // Build a context that will fail SaveChanges by disposing it first — every
        // method on a disposed context throws, including FindAsync. The catch-all in
        // BeatAsync must still swallow the exception.
        using var db = TestDbFactory.Create();
        db.Dispose();

        var act = () => SystemHealthWriter.BeatAsync(db, "TestService-Throw", 5);
        await act.Should().NotThrowAsync("SaveChanges failures must never propagate to the caller");

        // Even though the write failed, the debounce slot is set: a second call within
        // the window stays a no-op. We verify that by swapping in a working DB and
        // observing that no row gets written — proof that the second call short-circuits.
        using var goodDb = TestDbFactory.Create();
        await SystemHealthWriter.BeatAsync(goodDb, "TestService-Throw", 5, status: "should-not-land");
        (await goodDb.SystemHealth.FindAsync("TestService-Throw"))
            .Should().BeNull("the debounce slot was reserved by the first (failed) call, so the second is skipped");
    }
}
