using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Hosting;
using NodePilot.Core.Audit;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

public sealed class DatabaseRecoveryAuditServiceTests
{
    private sealed class FailOnceOnRecoveryAuditSaveInterceptor : SaveChangesInterceptor
    {
        private readonly DatabaseAvailabilityTracker _availability;
        private readonly bool _throwAfterCommit;
        private int _fired;
        internal TaskCompletionSource Fired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal FailOnceOnRecoveryAuditSaveInterceptor(
            DatabaseAvailabilityTracker availability,
            bool throwAfterCommit)
        {
            _availability = availability;
            _throwAfterCommit = throwAfterCommit;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_throwAfterCommit)
                MaybeThrow(eventData);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (_throwAfterCommit)
                MaybeThrow(eventData);
            return ValueTask.FromResult(result);
        }

        private void MaybeThrow(DbContextEventData eventData)
        {
            var writesRecoveryAudit = eventData.Context?.ChangeTracker.Entries<AuditLogEntry>()
                .Any(entry => entry.Entity.Action == "DATABASE_RECOVERED") == true;
            if (!writesRecoveryAudit || Interlocked.Exchange(ref _fired, 1) != 0)
                return;

            _availability.ReportUnreachable(DatabaseOutageReason.Unreachable);
            Fired.TrySetResult();
            throw new IOException(_throwAfterCommit
                ? "simulated lost acknowledgement after audit commit"
                : "simulated outage before audit commit");
        }
    }

    [Fact]
    public async Task RealOutageRecovery_PersistsExactlyOneEpisodeAudit_AndNoTripAudit()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using (var schema = NewDb(connection))
            await schema.Database.EnsureCreatedAsync();

        var availability = new DatabaseAvailabilityTracker(
            NullLogger<DatabaseAvailabilityTracker>.Instance,
            probeSuccessesToRecover: 1);
        var services = new ServiceCollection();
        services.AddScoped(_ => NewDb(connection));
        services.AddSingleton<IAuditStager, AuditStager>();
        await using var provider = services.BuildServiceProvider();
        var service = new DatabaseRecoveryAuditService(
            availability,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DatabaseRecoveryAuditService>.Instance);
        await service.StartAsync(CancellationToken.None);

        try
        {
            availability.MarkBootComplete();
            availability.ReportUnreachable(DatabaseOutageReason.Unreachable);
            availability.ReportUnreachable(DatabaseOutageReason.Wedged);
            await Task.Delay(100);

            var duringOutage = await ReadAuditRowsAsync(connection);
            duringOutage.Should().BeEmpty(
                "opening or updating an outage must never attempt an audit write while the database is down");

            availability.ReportProbeSucceeded();
            var rows = await WaitForAuditRowsAsync(connection, expectedCount: 1);
            availability.ReportProbeSucceeded();
            await Task.Delay(100);
            rows = await ReadAuditRowsAsync(connection);

            var recovered = rows.Should().ContainSingle().Subject;
            recovered.Action.Should().Be("DATABASE_RECOVERED");
            recovered.ResourceType.Should().Be("Database");
            recovered.UserId.Should().BeNull();
            recovered.Username.Should().BeNull();
            using var details = JsonDocument.Parse(recovered.Details!);
            details.RootElement.GetProperty("outageEpisodeId").GetInt64().Should().Be(1);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Fact]
    public async Task TwoRealOutageRecoveries_PersistOneDistinctAuditPerEpisode()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using (var schema = NewDb(connection))
            await schema.Database.EnsureCreatedAsync();

        var availability = new DatabaseAvailabilityTracker(
            NullLogger<DatabaseAvailabilityTracker>.Instance,
            probeSuccessesToRecover: 1);
        var services = new ServiceCollection();
        services.AddScoped(_ => NewDb(connection));
        services.AddSingleton<IAuditStager, AuditStager>();
        await using var provider = services.BuildServiceProvider();
        var service = new DatabaseRecoveryAuditService(
            availability,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DatabaseRecoveryAuditService>.Instance);
        await service.StartAsync(CancellationToken.None);

        try
        {
            availability.MarkBootComplete();
            for (var expectedEpisode = 1; expectedEpisode <= 2; expectedEpisode++)
            {
                availability.ReportUnreachable(DatabaseOutageReason.Unreachable);
                availability.Snapshot.OutageEpisodeId.Should().Be(expectedEpisode);
                availability.ReportProbeSucceeded(availability.Snapshot.OutageEpisodeId);
                _ = await WaitForAuditRowsAsync(connection, expectedEpisode);
                availability.ReportProbeSucceeded();
            }

            var rows = await ReadAuditRowsAsync(connection);
            rows.Should().HaveCount(2);
            rows.Select(row => row.Id).Should().OnlyHaveUniqueItems();
            rows.Select(EpisodeId).Should().Equal(1, 2);
            rows.Should().OnlyContain(row => row.Action == "DATABASE_RECOVERED");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryAuditWriteFailure_RetriesOnFreshContextWithoutDuplicatingUnknownCommit(
        bool commitBeforeAcknowledgementIsLost)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using (var schema = NewDb(connection))
            await schema.Database.EnsureCreatedAsync();

        var availability = new DatabaseAvailabilityTracker(
            NullLogger<DatabaseAvailabilityTracker>.Instance,
            probeSuccessesToRecover: 1);
        var failure = new FailOnceOnRecoveryAuditSaveInterceptor(
            availability,
            commitBeforeAcknowledgementIsLost);
        var contextsCreated = 0;
        var services = new ServiceCollection();
        services.AddScoped<NodePilotDbContext>(_ =>
        {
            Interlocked.Increment(ref contextsCreated);
            return new NodePilotDbContext(
                new DbContextOptionsBuilder<NodePilotDbContext>()
                    .UseSqlite(connection)
                    .AddInterceptors(failure)
                    .Options);
        });
        services.AddSingleton<IAuditStager, AuditStager>();
        await using var provider = services.BuildServiceProvider();
        var service = new DatabaseRecoveryAuditService(
            availability,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DatabaseRecoveryAuditService>.Instance);
        await service.StartAsync(CancellationToken.None);

        try
        {
            availability.MarkBootComplete();
            availability.ReportUnreachable(DatabaseOutageReason.Unreachable);
            availability.ReportProbeSucceeded(availability.Snapshot.OutageEpisodeId);
            await failure.Fired.Task.WaitAsync(TimeSpan.FromSeconds(5));

            availability.State.Should().Be(DatabaseAvailabilityState.Unavailable);
            availability.Snapshot.OutageEpisodeId.Should().Be(2,
                "the failed audit write opened a distinct second outage episode");
            availability.ReportProbeSucceeded(availability.Snapshot.OutageEpisodeId);

            var rows = await WaitForAuditRowsAsync(connection, expectedCount: 2);
            await Task.Delay(100);
            rows = await ReadAuditRowsAsync(connection);

            rows.Should().HaveCount(2,
                "the stable audit id must adjudicate a lost commit acknowledgement without a duplicate");
            rows.Select(EpisodeId).Order().Should().Equal(1, 2);
            rows.Select(row => row.Id).Should().OnlyHaveUniqueItems();
            contextsCreated.Should().BeGreaterThanOrEqualTo(3,
                "the failed attempt, its retry, and the next episode must each resolve a fresh DbContext");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    private static NodePilotDbContext NewDb(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).Options);

    private static async Task<List<AuditLogEntry>> WaitForAuditRowsAsync(
        SqliteConnection connection,
        int expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var rows = await ReadAuditRowsAsync(connection);
            if (rows.Count == expectedCount)
                return rows;
            await Task.Delay(25, timeout.Token);
        }
    }

    private static async Task<List<AuditLogEntry>> ReadAuditRowsAsync(SqliteConnection connection)
    {
        await using var observer = NewDb(connection);
        return await observer.AuditLog.AsNoTracking().OrderBy(entry => entry.Timestamp).ToListAsync();
    }

    private static long EpisodeId(AuditLogEntry entry)
    {
        using var details = JsonDocument.Parse(entry.Details!);
        return details.RootElement.GetProperty("outageEpisodeId").GetInt64();
    }
}
