using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Analysis;
using Xunit;

namespace NodePilot.Mcp.Tests.Analysis;

/// <summary>
/// Pins the equality contract of the by-id definition diff. The comparison is semantic
/// (<c>JsonNode.DeepEquals</c>): property order and number representation do not matter, array
/// order and actual value changes do.
/// </summary>
public sealed class DefinitionDiffTests
{
    private static JsonElement E(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Diff_SameNodeWithSwappedPropertyOrderAndNumberFormat_NotModified()
    {
        // Same node, three cosmetic differences only: top-level property order, nested property
        // order inside data/position, and 100 written as 100.0 / 200 as 2e2.
        var current = E("""
        {"nodes":[
          {"id":"a","type":"activity","position":{"x":100,"y":200},
           "data":{"label":"Check Disk","activityType":"runScript","config":{"timeoutSeconds":60}}}],
         "edges":[]}
        """);
        var proposed = E("""
        {"nodes":[
          {"data":{"config":{"timeoutSeconds":60.0},"activityType":"runScript","label":"Check Disk"},
           "position":{"y":2e2,"x":100.0},"type":"activity","id":"a"}],
         "edges":[]}
        """);

        var diff = DefinitionDiff.Diff(current, proposed);

        diff.Nodes.Added.Should().BeEmpty();
        diff.Nodes.Removed.Should().BeEmpty();
        diff.Nodes.Modified.Should().BeEmpty();
    }

    [Fact]
    public void Diff_ChangedConfigValue_ReportsModified()
    {
        var current = E("""
        {"nodes":[{"id":"a","data":{"config":{"timeoutSeconds":60}}}],"edges":[]}
        """);
        var proposed = E("""
        {"nodes":[{"id":"a","data":{"config":{"timeoutSeconds":90}}}],"edges":[]}
        """);

        var diff = DefinitionDiff.Diff(current, proposed);

        diff.Nodes.Modified.Should().Equal("a");
        diff.Nodes.Added.Should().BeEmpty();
        diff.Nodes.Removed.Should().BeEmpty();
    }

    [Fact]
    public void Diff_ReorderedArrayValue_ReportsModified()
    {
        // Arrays stay order-sensitive — only object property order is ignored.
        var current = E("""
        {"nodes":[{"id":"a","data":{"config":{"tags":["x","y"]}}}],"edges":[]}
        """);
        var proposed = E("""
        {"nodes":[{"id":"a","data":{"config":{"tags":["y","x"]}}}],"edges":[]}
        """);

        DefinitionDiff.Diff(current, proposed).Nodes.Modified.Should().Equal("a");
    }

    [Fact]
    public void Diff_AddedRemovedAndModified_AreReportedSortedById()
    {
        var current = E("""
        {"nodes":[{"id":"keep"},{"id":"gone"},{"id":"b","data":{"label":"old"}}],
         "edges":[{"id":"e1","source":"a","target":"b"}]}
        """);
        var proposed = E("""
        {"nodes":[{"id":"keep"},{"id":"zNew"},{"id":"aNew"},{"id":"b","data":{"label":"new"}}],
         "edges":[{"id":"e1","target":"b","source":"a"}]}
        """);

        var diff = DefinitionDiff.Diff(current, proposed);

        diff.Nodes.Added.Should().Equal("aNew", "zNew");
        diff.Nodes.Removed.Should().Equal("gone");
        diff.Nodes.Modified.Should().Equal("b");
        diff.Edges.Added.Should().BeEmpty();
        diff.Edges.Removed.Should().BeEmpty();
        diff.Edges.Modified.Should().BeEmpty();
    }

    [Fact]
    public void Diff_MissingArraysAndNonObjectItems_AreIgnored()
    {
        var current = E("""{"nodes":["not-an-object",{"noId":true},{"id":42}]}""");
        var proposed = E("""{"edges":[{"id":"e1"}]}""");

        var diff = DefinitionDiff.Diff(current, proposed);

        diff.Nodes.Added.Should().BeEmpty();
        diff.Nodes.Removed.Should().BeEmpty();
        diff.Nodes.Modified.Should().BeEmpty();
        diff.Edges.Added.Should().Equal("e1");
    }
}
