using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Configuration;
using NodePilot.Api.Services;
using NodePilot.Api.Services.Backup;
using NodePilot.Api.Services.Backup.Parts;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Services.Backup;

public class BackupSnapshotTests
{
    [Fact]
    public async Task Export_ConcurrentCredentialAndMachineCreation_RestoresIntoEmptyDestination()
    {
        const string passphrase = "snapshot-backup-passphrase";
        var databasePath = Path.Combine(Path.GetTempPath(), $"np-backup-snapshot-{Guid.NewGuid():N}.db");
        var runtimePath = Path.Combine(Path.GetTempPath(), $"np-backup-snapshot-{Guid.NewGuid():N}.json");
        var options = new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, DefaultTimeout = 1 }.ToString()).Options;
        var atRest = new AesGcmSecretProtector(new byte[32]);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await using var source = new NodePilotDbContext(options);
            await source.Database.EnsureCreatedAsync(ct);
            var existing = new Credential { Name = "existing", Username = "existing", EncryptedPassword = atRest.Protect("existing-secret") };
            source.Credentials.Add(existing);
            source.ManagedMachines.Add(new ManagedMachine { Name = "existing-host", Hostname = "existing.local", DefaultCredentialId = existing.Id });
            await source.SaveChangesAsync(ct);

            await using var writer = new NodePilotDbContext(options);
            var concurrent = new Credential { Name = "concurrent", Username = "concurrent", EncryptedPassword = atRest.Protect("concurrent-secret") };
            writer.Credentials.Add(concurrent);
            writer.ManagedMachines.Add(new ManagedMachine { Name = "concurrent-host", Hostname = "concurrent.local", DefaultCredentialId = concurrent.Id });
            var deferred = false;
            var attempted = false;
            var credentials = new AfterExportPart(new CredentialBackupPart(source, atRest), async () =>
            {
                attempted = true;
                try { await writer.SaveChangesAsync(ct); }
                catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
                {
                    deferred = true;
                }
                catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 5 or 6 })
                {
                    deferred = true;
                }
            });
            var export = new BackupService([credentials, new MachineBackupPart(source)], source);

            var backup = await export.ExportAsync([BackupSections.Machines], passphrase, "admin", ct);
            attempted.Should().BeTrue();
            if (deferred) await writer.SaveChangesAsync(ct);
            (await writer.ManagedMachines.CountAsync(ct)).Should().Be(2);
            source.Database.CurrentTransaction.Should().BeNull();

            using var destination = TestDbFactory.Create();
            var restore = new BackupRestoreService(destination, atRest,
                new RuntimeOverridesWriter(runtimePath, NullLogger<RuntimeOverridesWriter>.Instance),
                NullLogger<BackupRestoreService>.Instance,
                new WorkflowVersionDefinitionProtector(atRest, NullLogger<WorkflowVersionDefinitionProtector>.Instance));
            await restore.RestoreAsync(backup.Content, passphrase,
                new Dictionary<string, RestoreConflictPolicy>(), null, ct);

            var machines = await destination.ManagedMachines.ToListAsync(ct);
            var restoredCredentials = await destination.Credentials.ToListAsync(ct);
            machines.Should().NotBeEmpty();
            machines.Should().OnlyContain(machine => restoredCredentials.Any(credential => credential.Id == machine.DefaultCredentialId));
            machines.Count.Should().Be(backup.Counts.Single(count => count.Section == BackupSections.Machines).Count);
            restoredCredentials.Count.Should().Be(backup.Counts.Single(count => count.Section == BackupSections.Credentials).Count);
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(runtimePath);
        }
    }

    private sealed class AfterExportPart(IBackupPart inner, Func<Task> afterExport) : IBackupPart
    {
        public string Key => inner.Key;
        public IReadOnlyList<string> DependsOn => inner.DependsOn;
        public Task<int> CountAsync(CancellationToken ct) => inner.CountAsync(ct);
        public async Task<JsonNode> ExportAsync(BackupExportContext ctx, CancellationToken ct)
        {
            var content = await inner.ExportAsync(ctx, ct);
            await afterExport();
            return content;
        }
    }
}
