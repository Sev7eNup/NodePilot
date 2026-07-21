using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

public sealed class BootstrapAdminInvariantCoverageTests
{
    [Fact]
    public void FirstAdminCreation_RechecksStateInsideCrossNodeTransactionFence()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src", "NodePilot.Api", "Controllers", "AuthController.cs"));

        var method = source.IndexOf(
            "private async Task<BootstrapAdminCreation> TryCreateBootstrapAdminAsync",
            StringComparison.Ordinal);
        var localGate = source.IndexOf(
            "AdminAccountMutationGate.EnterLocalAsync", method, StringComparison.Ordinal);
        var executionStrategy = source.IndexOf(
            "strategy.ExecuteAsync", localGate, StringComparison.Ordinal);
        var serializable = source.IndexOf(
            "IsolationLevel.Serializable", executionStrategy, StringComparison.Ordinal);
        var transactionGate = source.IndexOf(
            "AdminAccountMutationGate.AcquireTransactionLockAsync", serializable,
            StringComparison.Ordinal);
        var usersRecheck = source.IndexOf(
            "_db.Users.AnyAsync", transactionGate, StringComparison.Ordinal);
        var tokenRecheck = source.IndexOf(
            "AdminBootstrap.Validate", usersRecheck, StringComparison.Ordinal);
        var insert = source.IndexOf("_db.Users.Add(created)", tokenRecheck, StringComparison.Ordinal);
        var commit = source.IndexOf("transaction.CommitAsync", insert, StringComparison.Ordinal);

        method.Should().BeGreaterThanOrEqualTo(0);
        localGate.Should().BeGreaterThan(method);
        executionStrategy.Should().BeGreaterThan(localGate);
        serializable.Should().BeGreaterThan(executionStrategy);
        transactionGate.Should().BeGreaterThan(serializable,
            "the SQL Server/PostgreSQL advisory lock must be reacquired inside every retry attempt");
        usersRecheck.Should().BeGreaterThan(transactionGate,
            "database emptiness must be decided only after the cross-node fence is held");
        tokenRecheck.Should().BeGreaterThan(usersRecheck,
            "the one-shot token must be re-read while the same fence is held");
        insert.Should().BeGreaterThan(tokenRecheck);
        commit.Should().BeGreaterThan(insert);
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
