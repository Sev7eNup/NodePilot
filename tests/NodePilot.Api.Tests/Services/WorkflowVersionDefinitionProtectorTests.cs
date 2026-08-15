using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Services;
using NodePilot.Core.Models;
using NodePilot.Data.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Services;

public sealed class WorkflowVersionDefinitionProtectorTests
{
    private static WorkflowVersionDefinitionProtector CreateProtector() =>
        new(new AesGcmSecretProtector(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            NullLogger<WorkflowVersionDefinitionProtector>.Instance);

    [Fact]
    public void Protect_StoresNoDefinitionLiteral_AndRoundTripsExactly()
    {
        const string definition =
            """{"nodes":[{"data":{"config":{"script":"ConvertTo-SecureString 'hunter2' -AsPlainText -Force"}}}],"edges":[]}""";
        var sut = CreateProtector();

        var stored = sut.Protect(definition);

        stored.Should().NotContain("hunter2");
        stored.Should().NotBe(definition);
        sut.Unprotect(stored).Should().Be(definition);
    }

    [Fact]
    public void Unprotect_LegacyPlaintext_ReturnsItDuringUpgrade()
    {
        const string legacy = """{"nodes":[],"edges":[]}""";
        CreateProtector().Unprotect(legacy).Should().Be(legacy);
    }

    [Fact]
    public async Task StartupCheck_DetectsLegacyPlaintext_WithoutMutatingIt()
    {
        await using var db = TestDbFactory.Create();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(new WorkflowVersion
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Version = 1, Name = "wf",
            DefinitionJson = """{"nodes":[{"data":{"config":{"body":"legacy-literal"}}}],"edges":[]}""",
        });
        await db.SaveChangesAsync();
        var sut = CreateProtector();

        (await sut.WarnIfExplicitMigrationRequiredAsync(db, CancellationToken.None)).Should().BeTrue();
        db.ChangeTracker.Clear();
        var stored = await db.WorkflowVersions.Select(v => v.DefinitionJson).SingleAsync();
        stored.Should().Contain("legacy-literal",
            "startup must remain read-only so updater rollback and mixed-version HA stay safe");
    }

    [Fact]
    public async Task ReencryptAllAsync_ReadsLegacyProvider_AndRewrapsWithActiveProvider()
    {
        var legacyAtRest = new AesGcmSecretProtector(
            Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var activeAtRest = new AesGcmSecretProtector(
            Enumerable.Range(33, 32).Select(i => (byte)i).ToArray());
        var legacyDefinitions = new WorkflowVersionDefinitionProtector(
            legacyAtRest, NullLogger<WorkflowVersionDefinitionProtector>.Instance);
        var migratingDefinitions = new WorkflowVersionDefinitionProtector(
            new MigratingSecretProtector(activeAtRest, legacyAtRest),
            NullLogger<WorkflowVersionDefinitionProtector>.Instance);
        const string definition =
            """{"nodes":[{"data":{"config":{"scorchRaw":{"secret":"legacy-key-literal"}}}}],"edges":[]}""";

        await using var db = TestDbFactory.Create();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(new WorkflowVersion
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Version = 1, Name = workflow.Name,
            DefinitionJson = legacyDefinitions.Protect(definition),
        });
        await db.SaveChangesAsync();

        var result = await migratingDefinitions.ReencryptAllAsync(db, CancellationToken.None);

        result.Rewritten.Should().Be(1);
        result.Skipped.Should().Be(0);
        db.ChangeTracker.Clear();
        var stored = await db.WorkflowVersions.Select(v => v.DefinitionJson).SingleAsync();
        var activeDefinitions = new WorkflowVersionDefinitionProtector(
            activeAtRest, NullLogger<WorkflowVersionDefinitionProtector>.Instance);
        activeDefinitions.Unprotect(stored).Should().Be(definition);
        var legacyRead = () => legacyDefinitions.Unprotect(stored);
        legacyRead.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    [Fact]
    public async Task ReencryptAllAsync_CorruptEnvelope_IsSkippedAndReported()
    {
        await using var db = TestDbFactory.Create();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var versionId = Guid.NewGuid();
        db.WorkflowVersions.Add(new WorkflowVersion
        {
            Id = versionId, WorkflowId = workflow.Id, Version = 4, Name = workflow.Name,
            DefinitionJson = "np:wfv:v1:not-base64",
        });
        await db.SaveChangesAsync();

        var result = await CreateProtector().ReencryptAllAsync(db, CancellationToken.None);

        result.Rewritten.Should().Be(0);
        result.Skipped.Should().Be(1);
        result.SkippedDetails.Should().ContainSingle(s =>
            s.Id == versionId && s.Name == "wf v4" && s.Reason == nameof(FormatException));
    }

    [Fact]
    public async Task ReencryptAllAsync_RetentionDeletesBehindCursor_DoNotSkipLaterRows()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(), $"nodepilot-version-rotation-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<NodePilot.Data.NodePilotDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;

        try
        {
            await using var db = new NodePilot.Data.NodePilotDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var workflow = new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = "{}" };
            db.Workflows.Add(workflow);
            db.WorkflowVersions.AddRange(Enumerable.Range(1, 205).Select(version => new WorkflowVersion
            {
                Id = Guid.NewGuid(), WorkflowId = workflow.Id, Version = version, Name = workflow.Name,
                DefinitionJson = $$"""{"nodes":[],"edges":[],"literal":"legacy-{{version}}"}""",
            }));
            await db.SaveChangesAsync();

            var deleted = 0;
            var logger = new BatchCallbackLogger(() =>
            {
                if (Interlocked.Exchange(ref deleted, 1) != 0) return;
                using var retentionDb = new NodePilot.Data.NodePilotDbContext(options);
                retentionDb.WorkflowVersions.Where(v => v.Version <= 50).ExecuteDelete();
            });
            var sut = new WorkflowVersionDefinitionProtector(
                new AesGcmSecretProtector(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
                logger);

            var result = await sut.ReencryptAllAsync(db, CancellationToken.None);

            result.Rewritten.Should().Be(205,
                "deleting rows behind a stable keyset cursor cannot shift later legacy rows out of the sweep");
            db.ChangeTracker.Clear();
            var remaining = await db.WorkflowVersions.OrderBy(v => v.Version)
                .Select(v => v.DefinitionJson).ToListAsync();
            remaining.Should().HaveCount(155);
            remaining.Should().OnlyContain(value => sut.IsProtected(value));
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private sealed class BatchCallbackLogger(Action callback) : ILogger<WorkflowVersionDefinitionProtector>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug
                && formatter(state, exception).StartsWith(
                    "Re-encrypted workflow-version batch", StringComparison.Ordinal))
                callback();
        }
    }
}
