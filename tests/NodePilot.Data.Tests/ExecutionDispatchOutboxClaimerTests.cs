using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data.Availability;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

public class ExecutionDispatchOutboxClaimerTests
{
    [Fact]
    public void SqlServerClaim_SetsItsOwnIsolationLevel_BecauseThePoolLeaksTheLastOne()
    {
        // Guards the SQL Server statement in CI, where the provider integration test below is skipped.
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "NodePilot.Data", "ExecutionDispatchOutboxClaimer.cs"));

        var sqlServerBranch = source.IndexOf("IsSqlServer()", StringComparison.Ordinal);
        var isolation = source.IndexOf(
            "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;", sqlServerBranch, StringComparison.Ordinal);
        // Match the hint itself; the comment above the statement also mentions READPAST.
        var readPast = source.IndexOf("WITH (UPDLOCK, READPAST", sqlServerBranch, StringComparison.Ordinal);

        sqlServerBranch.Should().BeGreaterThanOrEqualTo(0);
        isolation.Should().BeGreaterThan(sqlServerBranch,
            "the claim must not inherit whatever isolation level the pooled connection carries");
        readPast.Should().BeGreaterThan(isolation,
            "the isolation level has to be set before the statement that depends on it");
        source.Should().Contain("READCOMMITTEDLOCK",
            "RCSI still makes READ COMMITTED versioned, which READPAST cannot use");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NodePilot.slnx")))
        {
            directory = directory.Parent;
        }
        if (directory is null) throw new InvalidOperationException("Could not locate the repository root.");
        return directory.FullName;
    }

    [Theory]
    [Trait("Category", "DatabaseIntegration")]
    [InlineData("sqlserver", false)]
    [InlineData("sqlserver", true)]
    public async Task SqlServer_ClaimSurvivesAConnectionLeftAtSerializable(string provider, bool rcsi)
    {
        if (!ProviderTestDatabase.IsConfigured(provider)) Assert.Skip($"No isolated {provider} test server configured.");
        await using var database = await ProviderTestDatabase.CreateAsync(provider, rcsi);
        Guid[] ids;
        await using (var seed = database.CreateContext()) ids = await SeedAsync(seed, DateTime.UtcNow, 1);

        // Leave the only pooled connection at Serializable, as a committed Serializable transaction does.
        var pooled = database.PooledConnectionString;
        await using (var poisoner = new SqlConnection(pooled))
        {
            await poisoner.OpenAsync();
            await using var tx = await poisoner.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            await using var noop = poisoner.CreateCommand();
            noop.Transaction = (SqlTransaction)tx;
            noop.CommandText = "SELECT 1";
            await noop.ExecuteScalarAsync();
            await tx.CommitAsync();
        }

        await using var check = new SqlConnection(pooled);
        await check.OpenAsync();
        await using (var level = check.CreateCommand())
        {
            level.CommandText =
                "SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
            // 4 = Serializable. Otherwise the pool reset the level and this test proves nothing.
            ((int)(short)(await level.ExecuteScalarAsync())!).Should().Be(4,
                "the test needs a pooled connection left at Serializable");
        }
        await check.CloseAsync();

        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlServer(pooled).Options);
        var now = DateTime.UtcNow.AddMinutes(1);
        var claimed = await ExecutionDispatchOutboxClaimer.TryClaimAsync(
            db, now, now.AddMinutes(5), "test-owner", [], CancellationToken.None);
        claimed.Should().Be(ids[0]);
    }

    [Fact]
    public async Task Sqlite_ClaimsEligibleRowsInPriorityOrder_AndExpiresLeases()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var interceptor = new ClaimCommandObserver();
        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).AddInterceptors(interceptor).Options);
        await db.Database.EnsureCreatedAsync();
        db.Database.SetCommandTimeout(7);
        await AssertEligibilityAsync(db);
        interceptor.ScalarCalls.Should().Be(5);
        interceptor.LastTimeout.Should().Be(7);
    }

    [Theory]
    [Trait("Category", "DatabaseIntegration")]
    [InlineData("postgres", false)]
    [InlineData("sqlserver", false)]
    [InlineData("sqlserver", true)]
    public async Task Provider_ClaimsAreAtomic_AndRespectEligibility(string provider, bool rcsi)
    {
        if (!ProviderTestDatabase.IsConfigured(provider)) Assert.Skip($"No isolated {provider} test server configured.");
        await using var database = await ProviderTestDatabase.CreateAsync(provider, rcsi);
        await using (var db = database.CreateContext()) await AssertEligibilityAsync(db);

        var now = DateTime.UtcNow.AddMinutes(2);
        Guid[] ids;
        await using (var db = database.CreateContext())
        {
            // Remove the rows deliberately left leased by the eligibility scenario.
            await db.ExecutionDispatchOutbox.ExecuteDeleteAsync();
            ids = await SeedAsync(db, now, 12);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(async worker =>
        {
            await using var db = database.CreateContext();
            return await ExecutionDispatchOutboxClaimer.TryClaimAsync(
                db, now, now.AddMinutes(1), $"owner-{worker}", [], CancellationToken.None);
        }));
        var claimed = results.OfType<Guid>().ToList();
        claimed.Should().NotBeEmpty().And.OnlyHaveUniqueItems();
        // Skip-locked reads may temporarily find nothing while another claimant owns scan
        // locks. Once competitors finish, every remaining item must still be claimable.
        for (var i = 0; i < ids.Length; i++)
        {
            await using var db = database.CreateContext();
            var next = await ExecutionDispatchOutboxClaimer.TryClaimAsync(
                db, now, now.AddMinutes(1), "drain", [], CancellationToken.None);
            if (next is null) break;
            claimed.Add(next.Value);
        }
        claimed.Should().BeEquivalentTo(ids);
        await using var verify = database.CreateContext();
        (await verify.ExecutionDispatchOutbox.Select(x => x.AttemptCount).ToListAsync())
            .Should().OnlyContain(count => count == 1);
    }

    [Theory]
    [Trait("Category", "DatabaseIntegration")]
    [InlineData("postgres", false)]
    [InlineData("sqlserver", false)]
    [InlineData("sqlserver", true)]
    public async Task Provider_SkipsLockedFirstCandidate(string provider, bool rcsi)
    {
        if (!ProviderTestDatabase.IsConfigured(provider)) Assert.Skip($"No isolated {provider} test server configured.");
        await using var database = await ProviderTestDatabase.CreateAsync(provider, rcsi);
        var now = DateTime.UtcNow;
        Guid[] ids;
        await using (var db = database.CreateContext()) ids = await SeedAsync(db, now, 2);
        await using var locker = database.CreateContext();
        await using var transaction = await locker.Database.BeginTransactionAsync();
        var lockSql = provider == "postgres"
            ? "UPDATE \"ExecutionDispatchOutbox\" SET \"AttemptCount\" = \"AttemptCount\" WHERE \"ExecutionId\" = {0}"
            : "UPDATE [ExecutionDispatchOutbox] WITH (ROWLOCK) SET [AttemptCount] = [AttemptCount] WHERE [ExecutionId] = {0}";
        await locker.Database.ExecuteSqlRawAsync(lockSql, ids[0]);
        await using var claimant = database.CreateContext();
        claimant.Database.SetCommandTimeout(3);
        var claimed = await ExecutionDispatchOutboxClaimer.TryClaimAsync(
            claimant, now, now.AddMinutes(1), "other-owner", [], CancellationToken.None);
        claimed.Should().Be(ids[1]);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task LostClaimResponse_DoesNotReplayOrStartAnotherClaim()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var observer = new LostResponseInterceptor();
        var availability = new DatabaseAvailabilityTracker(NullLogger<DatabaseAvailabilityTracker>.Instance);
        availability.MarkBootComplete();
        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IExecutionStrategyFactory, RetryingTimeoutStrategyFactory>()
            .AddInterceptors(observer, new DatabaseCommandAvailabilityInterceptor(availability)).Options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var ids = await SeedAsync(db, now, 2);
        await Assert.ThrowsAsync<TimeoutException>(() => ExecutionDispatchOutboxClaimer.TryClaimAsync(
            db, now, now.AddMinutes(1), "lost-response", [], CancellationToken.None));
        observer.ScalarCalls.Should().Be(1);
        availability.State.Should().Be(DatabaseAvailabilityState.Armed,
            "raw claims must still report command timeouts through the availability interceptor");
        db.ChangeTracker.Clear();
        var rows = await db.ExecutionDispatchOutbox.OrderBy(x => x.CreatedAt).ToListAsync();
        rows[0].LeaseOwner.Should().Be("lost-response");
        rows[0].AttemptCount.Should().Be(1);
        rows[1].AttemptCount.Should().Be(0);
        observer.Throw = false;
        (await ExecutionDispatchOutboxClaimer.TryClaimAsync(
            db, now.AddMinutes(1), now.AddMinutes(2), "recovered", [], CancellationToken.None))
            .Should().Be(ids[0]);
    }

    private static async Task AssertEligibilityAsync(NodePilotDbContext db)
    {
        var now = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        var ids = await SeedAsync(db, now, 5);
        var rows = await db.ExecutionDispatchOutbox.OrderBy(x => x.CreatedAt).ToListAsync();
        var blocked = rows[0].WorkflowId;
        rows[1].Priority = Core.Interfaces.ExecutionDispatchPriority.Interactive;
        rows[2].AvailableAt = now.AddMinutes(1);
        rows[3].LeaseExpiresAt = now.AddMinutes(1);
        rows[4].LeaseExpiresAt = now.AddSeconds(-1);
        await db.SaveChangesAsync();
        (await Claim(now)).Should().Be(ids[1]);
        (await Claim(now)).Should().Be(ids[4]);
        (await Claim(now)).Should().BeNull();
        (await Claim(now.AddMinutes(1))).Should().Be(ids[1], "the priority item's lease has expired");
        (await Claim(now.AddMinutes(1))).Should().Be(ids[2], "future availability is now due");

        Task<Guid?> Claim(DateTime at) => ExecutionDispatchOutboxClaimer.TryClaimAsync(
            db, at, at.AddMinutes(1), "test-owner", [blocked], CancellationToken.None);
    }

    private static async Task<Guid[]> SeedAsync(NodePilotDbContext db, DateTime now, int count)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var workflow = new Workflow { Name = $"Queue-{Guid.NewGuid():N}", DefinitionJson = "{}" };
            var execution = new WorkflowExecution { Workflow = workflow, Status = ExecutionStatus.Pending, StartedAt = now };
            db.WorkflowExecutions.Add(execution);
            db.ExecutionDispatchOutbox.Add(new ExecutionDispatchOutboxItem
            {
                ExecutionId = execution.Id, Execution = execution, WorkflowId = workflow.Id,
                CreatedAt = now.AddSeconds(-count + i), AvailableAt = now,
                TriggeredBy = "manual", MissingWorkflowMessage = "missing", PreOwnershipFailurePrefix = "failed",
            });
            ids.Add(execution.Id);
        }
        await db.SaveChangesAsync();
        return ids.ToArray();
    }

    private class ClaimCommandObserver : DbCommandInterceptor
    {
        public int ScalarCalls { get; private set; }
        public int LastTimeout { get; private set; }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("NodePilot:ExecutionDispatchClaim", StringComparison.Ordinal))
            {
                ScalarCalls++;
                LastTimeout = command.CommandTimeout;
            }
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class LostResponseInterceptor : ClaimCommandObserver
    {
        public bool Throw { get; set; } = true;

        public override ValueTask<object?> ScalarExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, object? result,
            CancellationToken cancellationToken = default)
            => Throw && command.CommandText.Contains("NodePilot:ExecutionDispatchClaim", StringComparison.Ordinal)
                ? throw new TimeoutException("Simulated lost response after the committed claim.")
                : base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public sealed class RetryingTimeoutStrategyFactory(ExecutionStrategyDependencies dependencies) : IExecutionStrategyFactory
    {
        public IExecutionStrategy Create() => new RetryingTimeoutStrategy(dependencies);
    }

    private sealed class RetryingTimeoutStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, 3, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TimeoutException;
    }
}
