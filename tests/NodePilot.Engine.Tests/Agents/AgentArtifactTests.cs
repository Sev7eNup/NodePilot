using System.Text;
using NodePilot.Core.Agents;
using NodePilot.Engine.Agents;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

public sealed class AgentArtifactTests
{
    [Fact]
    public async Task ParallelTransfersReserveQuotaBeforeReadingAndReleaseFailedReservation()
    {
        await using var store = new AgentArtifactStore(Guid.NewGuid());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = store.CollectAsync("first", AgentOptions.MaxCollectedBytes, async (_, _, _) => {
            await release.Task;
            throw new IOException("transfer failed");
        }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<AgentBudgetExceededException>(() => store.CollectAsync("second", 1,
            (_, _, _) => throw new InvalidOperationException("Must reject before reading"), TestContext.Current.CancellationToken));
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => first);
        var next = await store.CollectAsync("next", 1, (_, _, _) => Task.FromResult(new byte[1]), TestContext.Current.CancellationToken);
        Assert.Single(store.Artifacts);
        Assert.Equal(1, next.Length);
    }
    [Fact]
    public void Workspaces_AreStableAcrossRestart_AndIsolatedBetweenInstallations()
    {
        var apiRoot = AgentArtifactStore.GetBaseDirectory(@"C:\NodePilot\api");
        Assert.Equal(apiRoot, AgentArtifactStore.GetBaseDirectory(@"c:\nodepilot\api\"));
        Assert.NotEqual(apiRoot, AgentArtifactStore.GetBaseDirectory(@"C:\NodePilot\tests"));
        Assert.Equal(AgentArtifactStore.BaseDirectory, AgentArtifactStore.GetBaseDirectory(AppContext.BaseDirectory));
    }

    [Fact]
    public async Task Collect_250Megabytes_UsesBoundedBlocksAndSharedQuota_AndDeletesRawData()
    {
        var store = new AgentArtifactStore(Guid.NewGuid());
        var root = store.Root;
        var maxBlock = 0;
        var calls = 0;
        await using (store)
        {
            var artifact = await store.CollectAsync("server:C:\\Windows\\Logs\\CBS\\CBS.log", AgentOptions.MaxCollectedBytes,
                (offset, count, _) =>
                {
                    maxBlock = Math.Max(maxBlock, count); calls++;
                    var block = new byte[count]; Array.Fill(block, (byte)'x');
                    if (offset + count == AgentOptions.MaxCollectedBytes)
                        Encoding.UTF8.GetBytes("\nLAST_ERROR 0x800f081f\n").CopyTo(block, count - 23);
                    return Task.FromResult(block);
                }, TestContext.Current.CancellationToken);
            Assert.Equal(AgentOptions.MaxCollectedBytes, new FileInfo(artifact.LocalPath).Length);
            Assert.InRange(maxBlock, 1, AgentArtifactStore.TransferBlockBytes);
            Assert.True(calls > 900);
            var result = await store.SearchAsync(artifact.Id, "LAST_ERROR", 5, TestContext.Current.CancellationToken);
            Assert.Contains("CBS.log:2:", result);
            Assert.Contains("0x800f081f", result);
            Assert.True(result.Length < 2000);
            await Assert.ThrowsAsync<AgentBudgetExceededException>(() => store.CollectAsync("second-member.log", 1,
                (_, _, _) => throw new InvalidOperationException("Quota must reject before transfer"), TestContext.Current.CancellationToken));
        }
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("utf16be")]
    public async Task Search_HandlesEncodingAndMatchesAcrossLongLineFragmentBoundaries(string encodingName)
    {
        var encoding = encodingName switch { "utf16" => Encoding.Unicode, "utf16be" => Encoding.BigEndianUnicode, _ => Encoding.UTF8 };
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("first\r\n" + new string('x', 8189) + "BOUNDARY Fehler äöü\r\nlast")).ToArray();
        await using var store = new AgentArtifactStore(Guid.NewGuid());
        var artifact = await store.CollectAsync("source.log", bytes.Length,
            (offset, count, _) => Task.FromResult(bytes.AsSpan((int)offset, count).ToArray()), TestContext.Current.CancellationToken);
        var result = await store.SearchAsync(artifact.Id, "BOUNDARY", 10, TestContext.Current.CancellationToken);
        Assert.Contains("source.log:2:", result);
        Assert.Contains("BOUNDARY Fehler äöü", result);
        Assert.True(result.Length < 1500);
    }

    [Fact]
    public async Task TruncatedOrCancelledTransfer_DeletesPartialFileAndDoesNotConsumeQuota()
    {
        await using var store = new AgentArtifactStore(Guid.NewGuid());
        await Assert.ThrowsAsync<IOException>(() => store.CollectAsync("changed.log", 4,
            (_, _, _) => Task.FromResult(Array.Empty<byte>()), TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CollectAsync("cancelled.log", 4,
            (_, _, _) => Task.FromResult(new byte[4]), cancelled.Token));
        Assert.Empty(Directory.GetFiles(store.Root));
        Assert.Empty(store.Artifacts);
    }

    [Theory]
    [InlineData("C:\\Logs2\\file.log")]
    [InlineData("C:\\Logs\\..\\Windows\\secret")]
    [InlineData("C:\\Logs\\ok.log:secret")]
    [InlineData("\\\\?\\C:\\Logs\\file.log")]
    [InlineData("relative.log")]
    public void FileTool_RejectsPathEscapes(string path)
        => Assert.Throws<UnauthorizedAccessException>(() => AgentFileTools.ValidatePath(path, ["C:\\Logs"]));

    [Fact]
    public void FileTool_AllowsNormalizedDescendant()
        => Assert.Equal("C:\\Logs\\child\\file.log", AgentFileTools.ValidatePath("C:\\Logs\\child\\file.log", ["C:\\Logs"]));
}
