using FluentAssertions;
using Xunit;

namespace NodePilot.Data.Tests;

/// <summary>
/// Covers <see cref="FolderTreeMutationLock"/>. Readers rely on <see cref="FolderTreeMutationLock.Epoch"/>
/// to notice a folder mutation that started while they read without holding the lock.
/// </summary>
public class FolderTreeMutationLockTests
{
    [Fact]
    public async Task BeginMutationAsync_AdvancesTheEpoch_AcquireAsyncDoesNot()
    {
        var tree = new FolderTreeMutationLock();
        var before = tree.Epoch;

        using (await tree.AcquireAsync(CancellationToken.None)) { }
        tree.Epoch.Should().Be(before, "a reader must not look like a mutation to other readers");

        using (await tree.BeginMutationAsync(CancellationToken.None)) { }
        tree.Epoch.Should().Be(before + 1);
    }

    [Fact]
    public async Task BeginMutationAsync_AdvancesTheEpochOnlyOnceItHoldsTheLock()
    {
        var tree = new FolderTreeMutationLock();
        var before = tree.Epoch;

        Task<IDisposable> mutation;
        using (await tree.AcquireAsync(CancellationToken.None))
        {
            mutation = tree.BeginMutationAsync(CancellationToken.None);
            mutation.IsCompleted.Should().BeFalse("a mutation waits for the reader holding the lock");
            tree.Epoch.Should().Be(before, "a reader that captured the epoch under the lock must see it unchanged");
        }

        using (await mutation.WaitAsync(TimeSpan.FromSeconds(5))) { }
        tree.Epoch.Should().Be(before + 1);
    }

    [Fact]
    public async Task AcquireSharedAsync_ReadersHoldTheLockTogether()
    {
        var tree = new FolderTreeMutationLock();

        using var first = await tree.AcquireSharedAsync(CancellationToken.None);
        using var second = await tree.AcquireSharedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AcquireSharedAsync_WaitsForARunningMutation()
    {
        var tree = new FolderTreeMutationLock();
        Task<IDisposable> reader;
        using (await tree.BeginMutationAsync(CancellationToken.None))
        {
            reader = tree.AcquireSharedAsync(CancellationToken.None);
            reader.IsCompleted.Should().BeFalse();
        }

        using (await reader.WaitAsync(TimeSpan.FromSeconds(5))) { }
    }

    [Fact]
    public async Task AcquireAsync_WaitsForEverySharedHolder()
    {
        var tree = new FolderTreeMutationLock();
        var first = await tree.AcquireSharedAsync(CancellationToken.None);
        var second = await tree.AcquireSharedAsync(CancellationToken.None);

        var writer = tree.AcquireAsync(CancellationToken.None);
        first.Dispose();
        writer.IsCompleted.Should().BeFalse("one shared holder is still active");
        second.Dispose();

        using (await writer.WaitAsync(TimeSpan.FromSeconds(5))) { }
    }

    [Fact]
    public async Task AcquireSharedAsync_QueuesBehindAWaitingMutation()
    {
        var tree = new FolderTreeMutationLock();
        var active = await tree.AcquireSharedAsync(CancellationToken.None);
        var writer = tree.AcquireAsync(CancellationToken.None);
        var lateReader = tree.AcquireSharedAsync(CancellationToken.None);

        lateReader.IsCompleted.Should().BeFalse("a waiting mutation must not be starved by new readers");
        active.Dispose();
        using (await writer.WaitAsync(TimeSpan.FromSeconds(5)))
            lateReader.IsCompleted.Should().BeFalse();

        using (await lateReader.WaitAsync(TimeSpan.FromSeconds(5))) { }
    }

    [Fact]
    public async Task AcquireAsync_CancelledWhileQueued_ReleasesTheReadersBehindIt()
    {
        var tree = new FolderTreeMutationLock();
        using var active = await tree.AcquireSharedAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var writer = tree.AcquireAsync(cts.Token);
        var lateReader = tree.AcquireSharedAsync(CancellationToken.None);
        lateReader.IsCompleted.Should().BeFalse();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer);
        using (await lateReader.WaitAsync(TimeSpan.FromSeconds(5))) { }
    }

    [Fact]
    public async Task AcquireSharedAsync_AlreadyCancelled_Throws()
    {
        var tree = new FolderTreeMutationLock();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tree.AcquireSharedAsync(cts.Token));
        using (await tree.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))) { }
    }

    [Fact]
    public async Task Lease_DisposedTwice_ReleasesOnce()
    {
        var tree = new FolderTreeMutationLock();
        var first = await tree.AcquireSharedAsync(CancellationToken.None);
        using var second = await tree.AcquireSharedAsync(CancellationToken.None);

        first.Dispose();
        first.Dispose();

        var writer = tree.AcquireAsync(CancellationToken.None);
        writer.IsCompleted.Should().BeFalse("the second shared holder must still count");
        second.Dispose();
        using (await writer.WaitAsync(TimeSpan.FromSeconds(5))) { }
    }
}
