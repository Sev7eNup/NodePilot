using FluentAssertions;
using NodePilot.Ai;
using NodePilot.Ai.Knowledge;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Ai.Tests.Knowledge;

public sealed class DocsKnowledgeReaderTests : IDisposable
{
    private readonly string _root;

    public DocsKnowledgeReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "npk-docs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "concepts"));
        Write("getting-started.md", "# Getting started\n\nRun pg_ctl then dotnet run.");
        Write("concepts/triggers.md", "# Triggers\n\nA webhookTrigger fires on an HTTP POST. webhookTrigger webhookTrigger.");
        Write("notes.txt", "not markdown — webhookTrigger");
    }

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private DocsKnowledgeReader Reader(int maxFileBytes = 262_144)
        => new(new StaticOptionsMonitor<AiKnowledgeOptions>(new AiKnowledgeOptions
        {
            DocsEnabled = true,
            DocsRootPath = _root,
            DocsMaxFileBytes = maxFileBytes,
            DocsMaxResults = 20,
        }));

    [Fact]
    public void IsAvailable_ReflectsRootExistence()
    {
        Reader().IsAvailable().Should().BeTrue();
        new DocsKnowledgeReader(new StaticOptionsMonitor<AiKnowledgeOptions>(
            new AiKnowledgeOptions { DocsRootPath = Path.Combine(_root, "gone") }))
            .IsAvailable().Should().BeFalse();
    }

    [Fact]
    public void Search_RanksMoreRelevantDocFirst_AndOnlyMarkdown()
    {
        var hits = Reader().Search("webhookTrigger");
        hits.Should().NotBeEmpty();
        // triggers.md mentions the term multiple times → ranked first; notes.txt is not markdown → excluded.
        hits[0].Path.Should().Be("concepts/triggers.md");
        hits.Select(h => h.Path).Should().NotContain(p => p.EndsWith(".txt"));
    }

    [Fact]
    public void Read_MarkdownFile_ReturnsContent()
    {
        var r = Reader().Read("concepts/triggers.md");
        r.Ok.Should().BeTrue();
        r.Content.Should().Contain("webhookTrigger");
    }

    [Fact]
    public void Read_NonMarkdown_IsRejected()
    {
        Reader().Read("notes.txt").Ok.Should().BeFalse();
    }

    [Fact]
    public void Read_Traversal_IsRejected()
    {
        Reader().Read("../secret.md").Ok.Should().BeFalse();
    }

    [Fact]
    public void Read_OversizedFile_IsRejected()
    {
        Write("big.md", "# Big\n" + new string('x', 4_000));
        Reader(maxFileBytes: 500).Read("big.md").Ok.Should().BeFalse();
    }

    [Fact]
    public void Search_MissingRoot_ReturnsEmpty()
    {
        new DocsKnowledgeReader(new StaticOptionsMonitor<AiKnowledgeOptions>(
            new AiKnowledgeOptions { DocsRootPath = Path.Combine(_root, "gone") }))
            .Search("webhookTrigger").Should().BeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
