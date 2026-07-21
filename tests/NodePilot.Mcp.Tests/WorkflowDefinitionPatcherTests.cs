using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NodePilot.Mcp.Mapping;
using Xunit;

namespace NodePilot.Mcp.Tests;

/// <summary>
/// Direct unit coverage for the merge-by-id patcher: ParseOps validation, the delete/unknown-op
/// arms of Apply, field backfill, secret protection (restore vs. reject), and MergeFull's
/// empty-array guard. No API/WireMock — pure JSON in, JSON out.
/// </summary>
public sealed class WorkflowDefinitionPatcherTests
{
    private static JsonElement E(string json) => JsonDocument.Parse(json).RootElement;

    // ---- ParseOps -----------------------------------------------------------

    [Fact]
    public void ParseOps_NotAnArray_Throws()
    {
        var act = () => WorkflowDefinitionPatcher.ParseOps(E("""{"op":"upsertNode"}"""));

        act.Should().Throw<ArgumentException>().WithMessage("*must be a JSON array*");
    }

    [Theory]
    [InlineData("[\"not-an-object\"]")]           // element isn't an object
    [InlineData("[{\"node\":{}}]")]               // missing 'op'
    [InlineData("[{\"op\":123}]")]                // 'op' isn't a string
    public void ParseOps_ElementMissingStringOp_Throws(string operations)
    {
        var act = () => WorkflowDefinitionPatcher.ParseOps(E(operations));

        act.Should().Throw<ArgumentException>().WithMessage("*string 'op'*");
    }

    [Fact]
    public void ParseOps_ExtractsOpNodeEdgeAndId()
    {
        var ops = WorkflowDefinitionPatcher.ParseOps(E("""
        [
          {"op":"upsertNode","node":{"id":"n1"}},
          {"op":"upsertEdge","edge":{"id":"e1"}},
          {"op":"deleteNode","id":"n2"}
        ]
        """));

        ops.Should().HaveCount(3);
        ops[0].Op.Should().Be("upsertNode");
        ops[0].Node.Should().NotBeNull();
        ops[0].Edge.Should().BeNull();
        ops[1].Edge.Should().NotBeNull();
        ops[2].Op.Should().Be("deleteNode");
        ops[2].Id.Should().Be("n2");
    }

    // ---- Apply: delete / unknown-op arms ------------------------------------

    [Fact]
    public void Apply_DeleteNode_RemovesNodeAndIncidentEdges()
    {
        var current = E("""
        {"nodes":[{"id":"a","x":1},{"id":"b","x":1},{"id":"c","x":1}],
         "edges":[
           {"id":"ab","source":"a","target":"b"},
           {"id":"bc","source":"b","target":"c"}]}
        """);
        var ops = WorkflowDefinitionPatcher.ParseOps(E("""[{"op":"deleteNode","id":"b"}]"""));

        var result = WorkflowDefinitionPatcher.Apply(current, ops);
        var json = result.Definition.ToJsonString();

        json.Should().NotContain("\"b\"");    // node b gone
        json.Should().NotContain("\"ab\"");   // edge into b dropped
        json.Should().NotContain("\"bc\"");   // edge out of b dropped
        json.Should().Contain("\"a\"").And.Contain("\"c\"");
    }

    [Fact]
    public void Apply_DeleteEdge_RemovesOnlyThatEdge()
    {
        var current = E("""
        {"nodes":[{"id":"a"},{"id":"b"}],
         "edges":[{"id":"ab","source":"a","target":"b"},{"id":"ab2","source":"a","target":"b"}]}
        """);
        var ops = WorkflowDefinitionPatcher.ParseOps(E("""[{"op":"deleteEdge","id":"ab"}]"""));

        var result = WorkflowDefinitionPatcher.Apply(current, ops);
        var json = result.Definition.ToJsonString();

        json.Should().NotContain("\"ab\"");
        json.Should().Contain("\"ab2\"");
    }

    [Fact]
    public void Apply_UnknownOp_Throws()
    {
        var ops = WorkflowDefinitionPatcher.ParseOps(E("""[{"op":"frobnicate"}]"""));

        var act = () => WorkflowDefinitionPatcher.Apply(E("""{"nodes":[],"edges":[]}"""), ops);

        act.Should().Throw<ArgumentException>().WithMessage("*unknown op 'frobnicate'*");
    }

    [Fact]
    public void Apply_DeleteNode_WithoutId_Throws()
    {
        var ops = WorkflowDefinitionPatcher.ParseOps(E("""[{"op":"deleteNode"}]"""));

        var act = () => WorkflowDefinitionPatcher.Apply(E("""{"nodes":[],"edges":[]}"""), ops);

        act.Should().Throw<ArgumentException>().WithMessage("*requires an 'id'*");
    }

    [Fact]
    public void Apply_UpsertNode_WithoutNode_Throws()
    {
        // op string present, but no 'node' object → RequireItem throws.
        var ops = WorkflowDefinitionPatcher.ParseOps(E("""[{"op":"upsertNode","id":"x"}]"""));

        var act = () => WorkflowDefinitionPatcher.Apply(E("""{"nodes":[],"edges":[]}"""), ops);

        act.Should().Throw<ArgumentException>().WithMessage("*requires a 'node'*");
    }

    // ---- Apply: upsert merge (backfill + secret protection) -----------------

    [Fact]
    public void Apply_UpsertExistingNode_BackfillsUntouchedFields_AndRestoresSecret()
    {
        var current = E("""
        {"nodes":[
          {"id":"n1","type":"activity","position":{"x":1,"y":2},
           "data":{"config":{"secret":"real-secret","keep":"kept"}}}],
         "edges":[]}
        """);
        // Upsert omits position/type and tries to change the secret + add a field.
        var ops = WorkflowDefinitionPatcher.ParseOps(E("""
        [{"op":"upsertNode","node":{"id":"n1","data":{"config":{"secret":"agent-secret","added":"v"}}}}]
        """));

        var result = WorkflowDefinitionPatcher.Apply(current, ops);
        var json = result.Definition.ToJsonString();

        json.Should().Contain("\"position\"");        // untouched field backfilled from original
        json.Should().Contain("\"type\":\"activity\"");
        json.Should().Contain("real-secret");          // existing secret restored
        json.Should().NotContain("agent-secret");      // agent's secret rejected
        json.Should().Contain("\"added\":\"v\"");       // caller's new non-secret field kept
        result.Notes.Should().Contain(n => n.Contains("Secret 'secret' not changed"));
    }

    [Fact]
    public void Apply_UpsertNewNode_WithInventedSecret_IsMaskedWithNote()
    {
        var current = E("""{"nodes":[],"edges":[]}""");
        var ops = WorkflowDefinitionPatcher.ParseOps(E("""
        [{"op":"upsertNode","node":{"id":"new","data":{"config":{"apiKey":"sk-live-xyz"}}}}]
        """));

        var result = WorkflowDefinitionPatcher.Apply(current, ops);
        var json = result.Definition.ToJsonString();

        json.Should().NotContain("sk-live-xyz");
        json.Should().Contain("***");
        result.Notes.Should().Contain(n => n.Contains("Secret 'apiKey' must be set manually"));
    }

    [Fact]
    public void Apply_UpsertEdge_AddsThenUpdatesById()
    {
        var current = E("""{"nodes":[],"edges":[{"id":"e1","source":"a","target":"b","data":{"label":"old"}}]}""");
        var ops = WorkflowDefinitionPatcher.ParseOps(E("""
        [
          {"op":"upsertEdge","edge":{"id":"e1","source":"a","target":"b","data":{"label":"new"}}},
          {"op":"upsertEdge","edge":{"id":"e2","source":"b","target":"c"}}
        ]
        """));

        var result = WorkflowDefinitionPatcher.Apply(current, ops);
        var edges = (JsonArray)result.Definition["edges"]!;

        edges.Should().HaveCount(2);
        result.Definition.ToJsonString().Should().Contain("new").And.NotContain("old");
        result.Definition.ToJsonString().Should().Contain("\"e2\"");
    }

    // ---- MergeFull ----------------------------------------------------------

    [Fact]
    public void MergeFull_ProposedMissingArrays_YieldsEmptyArrays()
    {
        var original = E("""{"nodes":[{"id":"n1"}],"edges":[{"id":"e1"}]}""");
        var proposed = E("""{}""");

        var result = WorkflowDefinitionPatcher.MergeFull(original, proposed);

        ((JsonArray)result.Definition["nodes"]!).Should().BeEmpty();
        ((JsonArray)result.Definition["edges"]!).Should().BeEmpty();
    }

    [Fact]
    public void MergeFull_RestoresSecretsFromOriginalByIdAndAddsNewNodes()
    {
        var original = E("""
        {"nodes":[{"id":"n1","data":{"config":{"password":"real-pw"}}}],"edges":[]}
        """);
        // Proposed re-sends n1 with a masked secret (as the agent only ever saw ***) and adds n2.
        var proposed = E("""
        {"nodes":[
          {"id":"n1","data":{"config":{"password":"***"}}},
          {"id":"n2","data":{"config":{}}}],
         "edges":[]}
        """);

        var result = WorkflowDefinitionPatcher.MergeFull(original, proposed);
        var json = result.Definition.ToJsonString();

        json.Should().Contain("real-pw");   // real secret restored onto n1
        json.Should().Contain("\"n2\"");     // new node carried through
        ((JsonArray)result.Definition["nodes"]!).Should().HaveCount(2);
    }

    [Fact]
    public void MergeFull_ContentMaskedHeadersString_RestoredByUniversalMaskRule()
    {
        // `headers` is not a secret key; its inline secret is masked by CONTENT to a whole "***".
        // The universal "***"-restore must bring the original back so the agent never wipes it.
        var original = E("""
        {"nodes":[{"id":"n1","data":{"config":{"url":"https://x","headers":"Authorization: Bearer sk-live-REAL"}}}],"edges":[]}
        """);
        var proposed = E("""
        {"nodes":[{"id":"n1","data":{"config":{"url":"https://y","headers":"***"}}}],"edges":[]}
        """);

        var result = WorkflowDefinitionPatcher.MergeFull(original, proposed);
        var cfg = result.Definition["nodes"]!.AsArray()[0]!["data"]!["config"]!;

        cfg["url"]!.GetValue<string>().Should().Be("https://y");                                 // changed
        cfg["headers"]!.GetValue<string>().Should().Be("Authorization: Bearer sk-live-REAL");    // restored
        result.Notes.Should().BeEmpty();
    }
}
