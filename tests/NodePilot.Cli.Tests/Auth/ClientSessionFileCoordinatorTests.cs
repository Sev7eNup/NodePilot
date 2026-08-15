using FluentAssertions;
using NodePilot.Core.Clients;
using Xunit;

namespace NodePilot.Cli.Tests.Auth;

public sealed class ClientSessionFileCoordinatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "np-session-lock-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task EquivalentSessionPathAndServerOrigin_ShareCancellableLock()
    {
        var canonicalPath = Path.Combine(_dir, "session-default.dat");
        var equivalentPath = Path.Combine(_dir, "nested", "..", "session-default.dat");
        using var owner = await ClientSessionFileCoordinator.AcquireRefreshLockAsync(
            canonicalPath, "https://NODEPILOT.EXAMPLE:443/api", CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        Func<Task> blocked = async () =>
        {
            using var _ = await ClientSessionFileCoordinator.AcquireRefreshLockAsync(
                equivalentPath, "https://nodepilot.example/another-path", cts.Token);
        };

        await blocked.Should().ThrowAsync<OperationCanceledException>();
        owner.Dispose();

        // The lock file may remain, but releasing/crashing the owner releases the OS handle.
        using var successor = await ClientSessionFileCoordinator.AcquireRefreshLockAsync(
            equivalentPath, "https://nodepilot.example", CancellationToken.None);
        successor.Should().NotBeNull();
    }
}
