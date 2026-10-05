using FluentAssertions;
using NodePilot.Scheduler.Sources;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// The parts of the event log trigger that decide how the log is read. The EventLog class itself
/// cannot be faked, so reading is tested through <see cref="EventLogTriggerSource.ReadFromEnd{T}"/>
/// with a list standing in for the log, and the pass scheduling through <see cref="SinglePassGate"/>.
/// </summary>
public class EventLogReadingTests
{
    // Record numbers are not positions: a log that has wrapped starts well above 1.
    private static readonly int[] Log = [101, 102, 103, 104, 105, 106];

    private static (int Max, List<int> Newer, int Reads) Read(int afterIndex, int[]? log = null)
    {
        log ??= Log;
        var reads = 0;
        var (max, newer) = EventLogTriggerSource.ReadFromEnd(log.Length, i => { reads++; return log[i]; }, e => e, afterIndex);
        return (max, newer, reads);
    }

    [Fact]
    public void ReadFromEnd_ReturnsOnlyEntriesAboveTheCursor_OldestFirst()
    {
        var (max, newer, _) = Read(afterIndex: 103);

        max.Should().Be(106);
        newer.Should().Equal(104, 105, 106);
    }

    [Fact]
    public void ReadFromEnd_ReadsOnlyWhatIsNewPlusOne_NotTheWholeLog()
    {
        var (_, newer, reads) = Read(afterIndex: 104);

        newer.Should().Equal(105, 106);
        reads.Should().Be(3, "two new entries and the first one at or below the cursor, which stops the scan");
    }

    [Fact]
    public void ReadFromEnd_NothingNew_ReadsExactlyTheNewestEntry()
    {
        var (max, newer, reads) = Read(afterIndex: 106);

        max.Should().Be(106);
        newer.Should().BeEmpty();
        reads.Should().Be(1);
    }

    [Fact]
    public void ReadFromEnd_NoCursorLimit_ReturnsTheWholeLog()
    {
        var (_, newer, _) = Read(afterIndex: int.MinValue);

        newer.Should().Equal(Log);
    }

    [Fact]
    public void ReadFromEnd_StartupWithoutCursor_ReturnsNothingButStillReportsTheTopIndex()
    {
        var (max, newer, reads) = Read(afterIndex: int.MaxValue);

        max.Should().Be(106);
        newer.Should().BeEmpty();
        reads.Should().Be(1);
    }

    [Fact]
    public void ReadFromEnd_CursorAheadOfTheLog_ReportsTheLowerTopIndexSoTheClearIsNoticed()
    {
        var (max, newer, _) = Read(afterIndex: 500);

        max.Should().Be(106);
        newer.Should().BeEmpty();
    }

    [Fact]
    public void ReadFromEnd_EmptyLog_ReportsZero()
    {
        var (max, newer, reads) = Read(afterIndex: 10, log: []);

        max.Should().Be(0);
        newer.Should().BeEmpty();
        reads.Should().Be(0);
    }

    [Theory]
    [InlineData(typeof(IndexOutOfRangeException))]
    [InlineData(typeof(ArgumentOutOfRangeException))]
    [InlineData(typeof(ArgumentException))]
    public void IsTransientReadFailure_TheEventLogRangeFailures_AreTransient(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;

        EventLogTriggerSource.IsTransientReadFailure(ex).Should().BeTrue();
        EventLogTriggerSource.IsTransientReadFailure(new AggregateException(ex)).Should().BeTrue();
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(System.IO.IOException))]
    [InlineData(typeof(NullReferenceException))]
    public void IsTransientReadFailure_AnythingElse_IsNotHiddenAsTransient(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;

        EventLogTriggerSource.IsTransientReadFailure(ex).Should().BeFalse();
        EventLogTriggerSource.IsTransientReadFailure(new AggregateException(new IndexOutOfRangeException(), ex)).Should().BeFalse();
    }

    [Fact]
    public async Task SinglePassGate_NeverRunsTwoPassesAtOnce_AndFoldsRequestsIntoOneFollowUp()
    {
        var gate = new SinglePassGate();
        var running = 0;
        var maxRunning = 0;
        var passes = 0;
        var firstPassEntered = new TaskCompletionSource();
        var releaseFirstPass = new TaskCompletionSource();

        async Task Pass()
        {
            var n = Interlocked.Increment(ref passes);
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            if (n == 1)
            {
                firstPassEntered.SetResult();
                await releaseFirstPass.Task;
            }
            Interlocked.Decrement(ref running);
        }

        var first = gate.RunAsync(Pass, CancellationToken.None);
        await firstPassEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Twenty requests arrive while the first pass is still running; none may start a pass.
        var burst = Enumerable.Range(0, 20).Select(_ => gate.RunAsync(Pass, CancellationToken.None)).ToArray();
        await Task.WhenAll(burst).WaitAsync(TimeSpan.FromSeconds(10));
        passes.Should().Be(1, "the requests only mark that another pass is due");

        releaseFirstPass.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        passes.Should().Be(2, "all twenty requests are covered by exactly one follow-up pass");
        maxRunning.Should().Be(1);
    }

    [Fact]
    public async Task SinglePassGate_UnderConcurrentRequests_NoRequestIsStranded()
    {
        // A stress check, not a proof: the window between "nothing pending" and "lock released" is
        // only nanoseconds wide and cannot be hit on purpose. Whatever the interleaving, a pass has
        // to start after the last request was made.
        var gate = new SinglePassGate();
        var lastRequestSeenByAPass = 0;
        var requests = 0;

        async Task Pass()
        {
            Volatile.Write(ref lastRequestSeenByAPass, Volatile.Read(ref requests));
            await Task.Yield();
        }

        var tasks = Enumerable.Range(0, 200).Select(_ => Task.Run(async () =>
        {
            Interlocked.Increment(ref requests);
            await gate.RunAsync(Pass, CancellationToken.None);
        })).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        Volatile.Read(ref lastRequestSeenByAPass).Should().Be(200, "a pass must have started after the last request was made");
    }

    [Fact]
    public async Task SinglePassGate_WhileHeld_RequestsRunNothingAndRunAfterRelease()
    {
        var gate = new SinglePassGate();
        var passes = 0;
        Task Pass() { Interlocked.Increment(ref passes); return Task.CompletedTask; }

        using (await gate.HoldAsync(CancellationToken.None))
        {
            await gate.RunAsync(Pass, CancellationToken.None);
            passes.Should().Be(0, "a held gate keeps passes away, as at startup");
        }

        await gate.RunAsync(Pass, CancellationToken.None);
        passes.Should().BeGreaterThanOrEqualTo(1);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
    }
}
