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
}
