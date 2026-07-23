using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

/// <summary>
/// <see cref="OperationalKnowledgeReader"/> — the read source behind the global AI Chat's
/// workflow-content tools: a workflow's secret-redacted definition and its computed scheduled-fire
/// forecast. (Listing workflows/executions/machines is handled by the database text2sql tools, not
/// this reader.) SQLite in-memory; RBAC via <see cref="AccessibleFolderSet"/>; redaction via the
/// deterministic <see cref="StubAuditDetailsRedactor"/> (masks <c>hunter2</c>).
/// </summary>
public class OperationalKnowledgeReaderTests
{
    private static readonly Guid FolderA = Guid.NewGuid();
    private static readonly Guid FolderB = Guid.NewGuid();

    private static OperationalKnowledgeReader NewReader(NodePilotDbContext db) => new(db, new StubAuditDetailsRedactor());

    private static AccessibleFolderSet Scoped(params Guid[] folders)
        => new() { IsUnrestricted = false, FolderIds = new HashSet<Guid>(folders) };

    private static Workflow SeedWorkflow(NodePilotDbContext db, string name, Guid folderId, string def = "{}")
    {
        // Workflow.FolderId has an FK to SharedWorkflowFolder — lazily seed the (non-root) folder so
        // callers can just pass an arbitrary folder GUID. The Local check dedups within one SaveChanges.
        if (folderId != SharedWorkflowFolder.RootFolderId && db.SharedWorkflowFolders.Local.All(f => f.Id != folderId))
        {
            var tag = folderId.ToString()[..8];
            db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
            {
                Id = folderId,
                Name = "folder-" + tag,
                ParentFolderId = SharedWorkflowFolder.RootFolderId,
                Path = "/folder-" + tag,
                Depth = 1,
            });
        }
        var wf = new Workflow { Id = Guid.NewGuid(), Name = name, FolderId = folderId, DefinitionJson = def, ActivityCount = 1 };
        db.Workflows.Add(wf);
        return wf;
    }

    // ---- GetWorkflowDefinitionAsync (redaction + name resolution) ------------------------------

    [Fact]
    public async Task GetWorkflowDefinitionAsync_RedactsKeyBasedAndInlineSecrets()
    {
        await using var db = TestDbFactory.Create();
        // "apiKey" is a secret config key → key-based masking; "hunter2" inside a runScript body is
        // caught only by the pattern-based IAuditDetailsRedactor pass.
        var def = """
            {"nodes":[{"id":"s1","data":{"activityType":"runScript","config":{"apiKey":"supersecret","script":"$p = 'hunter2'"}}}],"edges":[]}
            """;
        var wf = SeedWorkflow(db, "wf", FolderA, def);
        await db.SaveChangesAsync();

        var detail = await NewReader(db).GetWorkflowDefinitionAsync(AccessibleFolderSet.Unrestricted, "wf", CancellationToken.None);

        detail.Should().NotBeNull();
        detail!.RedactedDefinitionJson.Should().NotContain("supersecret"); // key-based (WorkflowSecretRedactor)
        detail.RedactedDefinitionJson.Should().NotContain("hunter2");      // pattern-based (IAuditDetailsRedactor)
        detail.RedactedDefinitionJson.Should().Contain("***");
    }

    [Fact]
    public async Task GetWorkflowDefinitionAsync_ByGuid_Works()
    {
        await using var db = TestDbFactory.Create();
        var wf = SeedWorkflow(db, "wf", FolderA);
        await db.SaveChangesAsync();

        var detail = await NewReader(db).GetWorkflowDefinitionAsync(AccessibleFolderSet.Unrestricted, wf.Id.ToString(), CancellationToken.None);
        detail.Should().NotBeNull();
        detail!.Id.Should().Be(wf.Id);
    }

    [Fact]
    public async Task GetWorkflowDefinitionAsync_AmbiguousName_ReturnsNull()
    {
        await using var db = TestDbFactory.Create();
        SeedWorkflow(db, "dup", FolderA);
        SeedWorkflow(db, "dup", FolderB);
        await db.SaveChangesAsync();

        var detail = await NewReader(db).GetWorkflowDefinitionAsync(AccessibleFolderSet.Unrestricted, "dup", CancellationToken.None);
        detail.Should().BeNull();
    }

    [Fact]
    public async Task GetWorkflowDefinitionAsync_OutOfScope_ReturnsNull()
    {
        await using var db = TestDbFactory.Create();
        SeedWorkflow(db, "secret-wf", FolderB);
        await db.SaveChangesAsync();

        var detail = await NewReader(db).GetWorkflowDefinitionAsync(Scoped(FolderA), "secret-wf", CancellationToken.None);
        detail.Should().BeNull();
    }

    // ---- ListScheduledFiresAsync (next-fire forecast) ------------------------------------------

    private static string ScheduleDef(string cron) =>
        """{"nodes":[{"id":"t1","type":"activity","data":{"activityType":"scheduleTrigger","config":{"cronExpression":"__CRON__"}}}],"edges":[]}"""
        .Replace("__CRON__", cron);

    private static Workflow SeedScheduled(NodePilotDbContext db, string name, Guid folderId, string cron, bool enabled = true)
    {
        var wf = SeedWorkflow(db, name, folderId, ScheduleDef(cron));
        wf.IsEnabled = enabled;
        return wf;
    }

    [Fact]
    public async Task ListScheduledFiresAsync_ReturnsUpcomingUtcFires_SpacedByCron()
    {
        await using var db = TestDbFactory.Create();
        SeedScheduled(db, "Every2Min", FolderA, "0 0/2 * * * ?");
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListScheduledFiresAsync(AccessibleFolderSet.Unrestricted, null, 3, 25, CancellationToken.None);

        var forecast = result.Should().ContainSingle().Subject;
        forecast.WorkflowName.Should().Be("Every2Min");
        forecast.NextFiresUtc.Should().HaveCount(3);
        forecast.NextFiresUtc.Should().OnlyContain(f => f.Kind == DateTimeKind.Utc);
        forecast.NextFiresUtc.Should().BeInAscendingOrder();
        // "0 0/2 …" fires on even minutes → consecutive fires are 2 minutes apart.
        (forecast.NextFiresUtc[1] - forecast.NextFiresUtc[0]).Should().Be(TimeSpan.FromMinutes(2));
        forecast.NextFiresUtc[0].Should().BeAfter(DateTime.UtcNow.AddSeconds(-1));
    }

    [Fact]
    public async Task ListScheduledFiresAsync_ExcludesDisabledWorkflows()
    {
        await using var db = TestDbFactory.Create();
        SeedScheduled(db, "Off", FolderA, "0 0/2 * * * ?", enabled: false);
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListScheduledFiresAsync(AccessibleFolderSet.Unrestricted, null, 3, 25, CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListScheduledFiresAsync_ScopedByFolder_ExcludesInaccessible()
    {
        await using var db = TestDbFactory.Create();
        SeedScheduled(db, "mine", FolderA, "0 0/2 * * * ?");
        SeedScheduled(db, "hidden", FolderB, "0 0/2 * * * ?");
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListScheduledFiresAsync(Scoped(FolderA), null, 1, 25, CancellationToken.None);
        result.Should().ContainSingle().Which.WorkflowName.Should().Be("mine");
    }

    [Fact]
    public async Task ListScheduledFiresAsync_IdOrNameFilter_ReturnsOnlyThatWorkflow()
    {
        await using var db = TestDbFactory.Create();
        SeedScheduled(db, "Alpha", FolderA, "0 0/2 * * * ?");
        SeedScheduled(db, "Beta", FolderA, "0 0/5 * * * ?");
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListScheduledFiresAsync(AccessibleFolderSet.Unrestricted, "Beta", 1, 25, CancellationToken.None);
        result.Should().ContainSingle().Which.WorkflowName.Should().Be("Beta");
    }

    [Fact]
    public async Task ListScheduledFiresAsync_InvalidCron_IsSkipped()
    {
        await using var db = TestDbFactory.Create();
        SeedScheduled(db, "Broken", FolderA, "not-a-cron");
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListScheduledFiresAsync(AccessibleFolderSet.Unrestricted, null, 3, 25, CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListScheduledFiresAsync_NonScheduleTrigger_ProducesNoForecast()
    {
        await using var db = TestDbFactory.Create();
        var def = """{"nodes":[{"id":"t1","type":"activity","data":{"activityType":"manualTrigger","config":{}}}],"edges":[]}""";
        var wf = SeedWorkflow(db, "Manual", FolderA, def);
        wf.IsEnabled = true;
        await db.SaveChangesAsync();

        var result = await NewReader(db).ListScheduledFiresAsync(AccessibleFolderSet.Unrestricted, null, 3, 25, CancellationToken.None);
        result.Should().BeEmpty();
    }
}
