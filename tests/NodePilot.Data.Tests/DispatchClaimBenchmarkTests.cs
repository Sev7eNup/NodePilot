using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Npgsql;
using Xunit;

namespace NodePilot.Data.Tests;

/// <summary>Explicit isolated-provider benchmark; the historical claim loop is frozen for comparison.</summary>
public class DispatchClaimBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "DatabaseBenchmark")]
    public async Task Postgres_IdleAndBurst_CompareHistoricalAndAtomicClaims()
    {
        if (!ProviderTestDatabase.IsConfigured("postgres")) Assert.Skip("No isolated PostgreSQL test server configured.");
        await using var database = await ProviderTestDatabase.CreateAsync("postgres");
        await using var prototype = database.CreateContext();
        var connectionString = new NpgsqlConnectionStringBuilder(prototype.Database.GetConnectionString())
        {
            Pooling = true, MaxPoolSize = 40, MinPoolSize = 0,
        }.ConnectionString;
        try
        {
            foreach (var atomic in new[] { false, true })
            {
                await MeasureAsync(CreateContext, atomic, burstSize: 0);
                await MeasureAsync(CreateContext, atomic, burstSize: 100);
            }
        }
        finally
        {
            using var connection = new NpgsqlConnection(connectionString);
            NpgsqlConnection.ClearPool(connection);
        }

        NodePilotDbContext CreateContext(ClaimCounter? counts)
            => new(new DbContextOptionsBuilder<NodePilotDbContext>()
                .UseNpgsql(connectionString, options => options.CommandTimeout(30))
                .AddInterceptors(counts is null ? [] : new IInterceptor[] { counts }).Options);
    }

    private async Task MeasureAsync(Func<ClaimCounter?, NodePilotDbContext> createContext, bool atomic, int burstSize)
    {
        const int workers = 20;
        var counts = new ClaimCounter();
        await using (var setup = createContext(null))
        {
            await setup.ExecutionDispatchOutbox.ExecuteDeleteAsync();
            var workflow = new Workflow { Id = Guid.NewGuid(), Name = "Dispatch benchmark", DefinitionJson = "{}" };
            setup.Workflows.Add(workflow);
            var created = DateTime.UtcNow;
            for (var i = 0; i < burstSize; i++)
            {
                var execution = new WorkflowExecution
                {
                    Id = Guid.NewGuid(), Workflow = workflow, Status = ExecutionStatus.Pending, StartedAt = created,
                };
                setup.WorkflowExecutions.Add(execution);
                setup.ExecutionDispatchOutbox.Add(new ExecutionDispatchOutboxItem
                {
                    Execution = execution, ExecutionId = execution.Id, WorkflowId = workflow.Id,
                    CreatedAt = created.AddTicks(i), AvailableAt = created,
                });
            }
            await setup.SaveChangesAsync();
        }
        var waits = new ConcurrentBag<double>();
        var ids = new ConcurrentBag<Guid>();
        var completed = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(burstSize == 0 ? TimeSpan.FromMilliseconds(3250) : TimeSpan.FromSeconds(30));
        using var gate = new SemaphoreSlim(1, 1);
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        if (burstSize > 0) signal.Writer.TryWrite(true);
        var clock = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, workers).Select(Worker));
        clock.Stop();
        var ordered = waits.Order().ToArray();
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().HaveCount(burstSize);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            mode = atomic ? "atomic_shared_idle_gate" : "historical_select_cas_per_worker",
            provider = "postgres", pooling = true, maximumPoolSize = 40, workers, burstSize, simulatedExecutionMs = 25,
            elapsedMs = Math.Round(clock.Elapsed.TotalMilliseconds, 1),
            claimStatements = counts.Count, starts = ids.Count,
            idleStatementsPerSecond = burstSize == 0 ? Math.Round(counts.Count / clock.Elapsed.TotalSeconds, 2) : (double?)null,
            claimStatementsPerStart = burstSize > 0 ? Math.Round((double)counts.Count / ids.Count, 2) : (double?)null,
            burstReleaseToClaimP50Ms = Percentile(0.5), burstReleaseToClaimP95Ms = Percentile(0.95),
        }));

        double? Percentile(double rank) => ordered.Length == 0 ? null
            : Math.Round(ordered[(int)Math.Ceiling(ordered.Length * rank) - 1], 1);

        async Task Worker(int worker)
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    Guid? claimed;
                    if (atomic)
                    {
                        await gate.WaitAsync(stop.Token);
                        try
                        {
                            do
                            {
                                stop.Token.ThrowIfCancellationRequested();
                                claimed = await ClaimAsync();
                                if (claimed is null) await WaitForSignal();
                            } while (claimed is null);
                        }
                        finally { gate.Release(); }
                    }
                    else
                    {
                        claimed = await ClaimAsync();
                        if (claimed is null)
                        {
                            await WaitForSignal();
                            continue;
                        }
                    }
                    waits.Add(clock.Elapsed.TotalMilliseconds);
                    ids.Add(claimed.Value);
                    await Task.Delay(25, stop.Token);
                    await using var cleanup = createContext(null);
                    await cleanup.ExecutionDispatchOutbox.Where(x => x.ExecutionId == claimed.Value).ExecuteDeleteAsync(stop.Token);
                    if (Interlocked.Increment(ref completed) == burstSize) await stop.CancelAsync();
                    signal.Writer.TryWrite(true);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

            async Task<Guid?> ClaimAsync()
            {
                await using var db = createContext(counts);
                var now = DateTime.UtcNow;
                var owner = $"benchmark-{worker}";
                if (atomic)
                    return await ExecutionDispatchOutboxClaimer.TryClaimAsync(
                        db, now, now.AddMinutes(1), owner, [], stop.Token);
                var candidates = await db.ExecutionDispatchOutbox.AsNoTracking()
                    .Where(x => x.AvailableAt <= now && (x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now))
                    .OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAt)
                    .Select(x => x.ExecutionId).Take(Math.Max(4, workers)).ToListAsync(stop.Token);
                foreach (var id in candidates)
                {
                    var changed = await db.ExecutionDispatchOutbox
                        .Where(x => x.ExecutionId == id && x.AvailableAt <= now
                            && (x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now))
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseOwner, owner)
                            .SetProperty(x => x.LeaseExpiresAt, now.AddMinutes(1))
                            .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1), stop.Token);
                    if (changed == 1) return id;
                }
                return null;
            }
        }

        async Task WaitForSignal()
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            wait.CancelAfter(1000);
            try { await signal.Reader.ReadAsync(wait.Token); }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
        }
    }

    private sealed class ClaimCounter : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            CountClaim(command);
            return base.ReaderExecutedAsync(command, eventData, result, ct);
        }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, int result, CancellationToken ct = default)
        {
            CountClaim(command);
            return base.NonQueryExecutedAsync(command, eventData, result, ct);
        }
        public override ValueTask<object?> ScalarExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, object? result, CancellationToken ct = default)
        {
            CountClaim(command);
            return base.ScalarExecutedAsync(command, eventData, result, ct);
        }

        private void CountClaim(DbCommand command)
        {
            if (command.CommandText.Contains("ExecutionDispatchOutbox", StringComparison.Ordinal))
                Interlocked.Increment(ref Count);
        }
    }
}
