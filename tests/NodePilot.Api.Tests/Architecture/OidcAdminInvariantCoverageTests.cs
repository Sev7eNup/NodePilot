using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

public sealed class OidcAdminInvariantCoverageTests
{
    [Fact]
    public void ExistingUserReconciliation_UsesLocalAndPerAttemptDatabaseAdminFence()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "NodePilot.Api", "Security", "Oidc", "OidcIdentityMapper.cs"));

        var reconcile = source.IndexOf("ReconcileExistingAsync(", StringComparison.Ordinal);
        var localGate = source.IndexOf(
            "AdminAccountMutationGate.EnterLocalAsync", reconcile, StringComparison.Ordinal);
        var executionStrategy = source.IndexOf(
            "strategy.ExecuteAsync", reconcile, StringComparison.Ordinal);
        var serializable = source.IndexOf(
            "IsolationLevel.Serializable", executionStrategy, StringComparison.Ordinal);
        var transactionGate = source.IndexOf(
            "AdminAccountMutationGate.AcquireTransactionLockAsync", serializable,
            StringComparison.Ordinal);

        reconcile.Should().BeGreaterThanOrEqualTo(0);
        localGate.Should().BeGreaterThan(reconcile);
        executionStrategy.Should().BeGreaterThan(localGate,
            "the process-local gate must cover every execution-strategy attempt");
        serializable.Should().BeGreaterThan(executionStrategy);
        transactionGate.Should().BeGreaterThan(serializable,
            "the cross-node advisory lock must be reacquired inside every transaction attempt");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NodePilot.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Could not locate NodePilot.slnx from the test output directory.");
    }
}
