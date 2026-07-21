using System.Text.Json;
using FluentAssertions;
using NodePilot.Ai.Knowledge;
using NodePilot.Core.Interfaces;
using Xunit;

namespace NodePilot.Ai.Tests.Knowledge;

public class KnowledgeChatToolRegistryTests
{
    // Minimal in-test readers — the registry's gating is over flags/privilege, not file IO.
    private sealed class FakeDocs : IDocsKnowledgeReader
    {
        public bool Available { get; set; } = true;
        public bool IsAvailable() => Available;
        public IReadOnlyList<KnowledgeSearchHit> Search(string query) => new[] { new KnowledgeSearchHit("getting-started.md", "…snippet…") };
        public KnowledgeFileResult Read(string relPath) => KnowledgeFileResult.Success(relPath, "# Doc");
    }

    private sealed class FakeSource : ISourceCodeKnowledgeReader
    {
        public bool Available { get; set; } = true;
        public bool IsAvailable() => Available;
        public IReadOnlyList<KnowledgeSearchHit> Search(string query) => new[] { new KnowledgeSearchHit("src/Program.cs", "…code…") };
        public KnowledgeFileResult Read(string relPath) => KnowledgeFileResult.Success(relPath, "class Program {}");
    }

    private sealed class FakeSettings : ISettingsKnowledgeReader
    {
        public List<SettingsSectionKnowledge> Sections { get; } = new()
        {
            new("Engine", "Engine Concurrency", false,
                JsonDocument.Parse("""{"runspace":{"minRunspaces":256,"maxRunspaces":768}}""").RootElement.Clone()),
            new("Smtp", "SMTP", true,
                JsonDocument.Parse("""{"host":"localhost","password":"********"}""").RootElement.Clone()),
        };
        public IReadOnlyList<SettingsSectionKnowledge> GetRedactedSnapshot() => Sections;
    }

    private static KnowledgeChatToolRegistry Registry(out FakeDocs docs, out FakeSource src)
    {
        docs = new FakeDocs();
        src = new FakeSource();
        return new KnowledgeChatToolRegistry(docs, src);
    }

    private static KnowledgeToolContext Ctx(
        bool docs = true, bool op = true, bool src = true, bool priv = true,
        FakeOperationalKnowledgeReader? opReader = null, ISettingsKnowledgeReader? settings = null)
        => new(AccessibleFolderSet.Unrestricted, priv, docs, op, src,
               op ? (opReader ?? new FakeOperationalKnowledgeReader()) : null,
               priv ? (settings ?? new FakeSettings()) : null);

    private static HashSet<string> ToolNames(KnowledgeChatToolRegistry reg, KnowledgeToolContext ctx)
        => reg.GetTools(ctx).Select(t => t.Name).ToHashSet();

    // ---- GetTools gating matrix ----------------------------------------------------------------

    [Fact]
    public void GetTools_AllEnabledPrivileged_ExposesEveryTool()
    {
        var reg = Registry(out _, out _);
        var names = ToolNames(reg, Ctx());
        names.Should().Contain(new[]
        {
            "search_docs", "read_doc",
            "list_workflows", "get_workflow_definition", "analyze_workflow",
            "list_recent_executions", "list_workflow_executions", "list_machines",
            "get_next_scheduled_fires",
            "search_source", "read_source",
            "read_settings",
        });
    }

    [Fact]
    public void GetTools_DocsDisabled_OmitsDocTools()
    {
        var reg = Registry(out _, out _);
        var names = ToolNames(reg, Ctx(docs: false));
        names.Should().NotContain("search_docs").And.NotContain("read_doc");
        names.Should().Contain("list_workflows");
    }

    [Fact]
    public void GetTools_OperationalDisabled_OmitsAllOperationalTools()
    {
        var reg = Registry(out _, out _);
        var names = ToolNames(reg, Ctx(op: false));
        names.Should().NotContain(new[] { "list_workflows", "get_workflow_definition", "analyze_workflow", "list_recent_executions", "list_workflow_executions", "list_machines", "get_next_scheduled_fires" });
        names.Should().Contain("search_docs");
    }

    [Fact]
    public void GetTools_OperationalButNotPrivileged_HidesWorkflowContentTools()
    {
        var reg = Registry(out _, out _);
        var names = ToolNames(reg, Ctx(priv: false, src: false));
        // Non-privileged keeps list/status tools but NOT definition/analysis (content).
        names.Should().Contain("list_workflows").And.Contain("list_recent_executions").And.Contain("list_machines");
        names.Should().NotContain("get_workflow_definition").And.NotContain("analyze_workflow");
    }

    [Fact]
    public void GetTools_SourceRequiresEnabledAndPrivilege()
    {
        var reg = Registry(out _, out _);
        ToolNames(reg, Ctx(src: true, priv: true)).Should().Contain("search_source").And.Contain("read_source");
        ToolNames(reg, Ctx(src: false, priv: true)).Should().NotContain("search_source");
        ToolNames(reg, Ctx(src: true, priv: false)).Should().NotContain("search_source");
    }

    [Fact]
    public void GetTools_ReadSettings_RequiresPrivilege()
    {
        var reg = Registry(out _, out _);
        // Available to Admin/Operator regardless of the docs/operational/source toggles …
        ToolNames(reg, Ctx(docs: false, op: false, src: false, priv: true)).Should().Contain("read_settings");
        // … and never to a non-privileged (Viewer) session.
        ToolNames(reg, Ctx(priv: false, src: false)).Should().NotContain("read_settings");
    }

    // ---- ExecuteAsync gating (defense in depth) ------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_UnknownTool_ReturnsErrorJson()
    {
        var reg = Registry(out _, out _);
        var r = await reg.ExecuteAsync("delete_everything", "{}", Ctx(), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_GatedOffTool_ReturnsErrorWithoutTouchingReader()
    {
        var reg = Registry(out _, out _);
        // search_source is not permitted when source is disabled — even if the model calls it.
        var r = await reg.ExecuteAsync("search_source", """{"query":"x"}""", Ctx(src: false), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.GetProperty("error").GetString().Should().Contain("nicht verfügbar");
    }

    [Fact]
    public async Task ExecuteAsync_WorkflowContentTool_NotPrivileged_IsDenied()
    {
        var reg = Registry(out _, out _);
        var r = await reg.ExecuteAsync("get_workflow_definition", """{"idOrName":"wf"}""", Ctx(priv: false, src: false), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.GetProperty("error").GetString().Should().Contain("nicht verfügbar");
    }

    [Fact]
    public async Task ExecuteAsync_ReadSettings_NotPrivileged_IsDenied()
    {
        var reg = Registry(out _, out _);
        var r = await reg.ExecuteAsync("read_settings", "{}", Ctx(priv: false, src: false), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.GetProperty("error").GetString().Should().Contain("nicht verfügbar");
    }

    // ---- ExecuteAsync read_settings ------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ReadSettings_ReturnsRedactedSections()
    {
        var reg = Registry(out _, out _);
        var r = await reg.ExecuteAsync("read_settings", "{}", Ctx(), CancellationToken.None);
        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(2);
        // Values are embedded as nested JSON (not string-escaped) and keep the masked secret.
        r.Should().Contain("minRunspaces").And.Contain("********");
        doc.RootElement.GetProperty("sections")[0].GetProperty("values").GetProperty("runspace")
            .GetProperty("minRunspaces").GetInt32().Should().Be(256);
    }

    [Fact]
    public async Task ExecuteAsync_ReadSettings_SectionFilter_NarrowsResult()
    {
        var reg = Registry(out _, out _);
        var r = await reg.ExecuteAsync("read_settings", """{"section":"Engine"}""", Ctx(), CancellationToken.None);
        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("sections")[0].GetProperty("section").GetString().Should().Be("Engine");
    }

    // ---- ExecuteAsync happy paths --------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_SearchDocs_ReturnsHits()
    {
        var reg = Registry(out _, out _);
        var r = await reg.ExecuteAsync("search_docs", """{"query":"trigger"}""", Ctx(), CancellationToken.None);
        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("hits")[0].GetProperty("path").GetString().Should().Be("getting-started.md");
    }

    [Fact]
    public async Task ExecuteAsync_SearchDocs_RootUnavailable_ReturnsError()
    {
        var reg = Registry(out var docs, out _);
        docs.Available = false;
        var r = await reg.ExecuteAsync("search_docs", """{"query":"x"}""", Ctx(), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.GetProperty("error").GetString().Should().Contain("nicht verfügbar");
    }

    [Fact]
    public async Task ExecuteAsync_GetWorkflowDefinition_PassesNameAndReturnsDetail()
    {
        var reg = Registry(out _, out _);
        var op = new FakeOperationalKnowledgeReader
        {
            Definition = new WorkflowKnowledgeDetail(Guid.NewGuid(), "Nightly Backup", "desc", true, """{"nodes":[],"edges":[]}"""),
        };
        var r = await reg.ExecuteAsync("get_workflow_definition", """{"idOrName":"Nightly Backup"}""", Ctx(opReader: op), CancellationToken.None);

        op.LastIdOrName.Should().Be("Nightly Backup");
        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("name").GetString().Should().Be("Nightly Backup");
        doc.RootElement.GetProperty("definition").GetString().Should().Contain("nodes");
    }

    [Fact]
    public async Task ExecuteAsync_GetWorkflowDefinition_NotFound_ReturnsError()
    {
        var reg = Registry(out _, out _);
        var op = new FakeOperationalKnowledgeReader { Definition = null };
        var r = await reg.ExecuteAsync("get_workflow_definition", """{"idOrName":"ghost"}""", Ctx(opReader: op), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_AnalyzeWorkflow_RunsAnalyzerOnRedactedDefinition()
    {
        var reg = Registry(out _, out _);
        var op = new FakeOperationalKnowledgeReader
        {
            // A trigger + an orphan (unconnected) activity → the analyzer flags the orphan.
            Definition = new WorkflowKnowledgeDetail(Guid.NewGuid(), "wf", null, true, """
                {"nodes":[
                  {"id":"t1","type":"activity","data":{"activityType":"scheduleTrigger","config":{}}},
                  {"id":"lonely","type":"activity","data":{"activityType":"log","config":{}}}
                ],"edges":[]}
                """),
        };
        var r = await reg.ExecuteAsync("analyze_workflow", """{"idOrName":"wf"}""", Ctx(opReader: op), CancellationToken.None);

        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        r.Should().Contain("orphan-node");
    }

    [Fact]
    public async Task ExecuteAsync_ListRecentExecutions_PassesStatusFilter()
    {
        var reg = Registry(out _, out _);
        var op = new FakeOperationalKnowledgeReader();
        await reg.ExecuteAsync("list_recent_executions", """{"status":"Failed","take":5}""", Ctx(opReader: op), CancellationToken.None);
        op.LastStatus.Should().Be("Failed");
        op.LastTake.Should().Be(5);
    }

    [Fact]
    public async Task ExecuteAsync_GetNextScheduledFires_PassesArgsAndReturnsUtcSchedules()
    {
        var reg = Registry(out _, out _);
        var op = new FakeOperationalKnowledgeReader();
        op.ScheduledFires.Add(new ScheduledFireForecast(
            Guid.NewGuid(), "Nightly Backup", "0 0 2 * * ?", "at 02:00",
            new[] { new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc) }));

        var r = await reg.ExecuteAsync("get_next_scheduled_fires", """{"idOrName":"Nightly Backup","count":3}""", Ctx(opReader: op), CancellationToken.None);

        op.LastIdOrName.Should().Be("Nightly Backup");
        op.LastPerWorkflow.Should().Be(3);
        using var doc = JsonDocument.Parse(r);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("schedules")[0].GetProperty("workflowName").GetString().Should().Be("Nightly Backup");
        // Serialized UTC instant must carry the 'Z' designator so the model reads it as UTC.
        doc.RootElement.GetProperty("schedules")[0].GetProperty("nextFiresUtc")[0].GetString().Should().EndWith("Z");
    }

    [Fact]
    public async Task ExecuteAsync_GetNextScheduledFires_NotPrivileged_StillAllowed()
    {
        var reg = Registry(out _, out _);
        var op = new FakeOperationalKnowledgeReader();
        // Schedule times are non-privileged operational data (like list_workflows) — a Viewer may ask.
        var r = await reg.ExecuteAsync("get_next_scheduled_fires", "{}", Ctx(priv: false, src: false, opReader: op), CancellationToken.None);
        JsonDocument.Parse(r).RootElement.TryGetProperty("error", out _).Should().BeFalse();
    }
}
