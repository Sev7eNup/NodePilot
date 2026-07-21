using NodePilot.Ai;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace NodePilot.Ai.Tests;

public class WorkflowDefinitionMergeTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ---- Field preservation (layout / semantics) -------------------------------------

    [Fact]
    public void Merge_UnchangedFields_BackfilledFromOriginal_WhenAiOmitsThem()
    {
        var original = Parse("""
            {
              "nodes": [
                { "id": "n1", "type": "activity", "position": { "x": 42, "y": 99 },
                  "parentId": "g1", "width": 200, "height": 80,
                  "data": { "label": "Old", "activityType": "runScript",
                            "credentialId": "cred-123", "targetMachineId": "m-1",
                            "config": { "script": "Get-Service", "timeoutSeconds": 60 } } }
              ],
              "edges": []
            }
            """);
        // The AI only changes the label, omitting position/credentialId/the remaining config fields.
        var proposed = Parse("""
            {
              "nodes": [
                { "id": "n1", "type": "activity",
                  "data": { "label": "New", "activityType": "runScript",
                            "config": { "script": "Restart-Service Spooler" } } }
              ],
              "edges": []
            }
            """);

        var merged = WorkflowDefinitionMerge.Merge(original, proposed).Definition;
        var node = (merged["nodes"]!.AsArray())[0]!;

        node["data"]!["label"]!.GetValue<string>().Should().Be("New");                 // AI's change wins
        node["position"]!["x"]!.GetValue<int>().Should().Be(42);                       // preserved
        node["position"]!["y"]!.GetValue<int>().Should().Be(99);
        node["parentId"]!.GetValue<string>().Should().Be("g1");                        // preserved
        node["data"]!["credentialId"]!.GetValue<string>().Should().Be("cred-123");     // preserved
        node["data"]!["targetMachineId"]!.GetValue<string>().Should().Be("m-1");       // preserved
        node["data"]!["config"]!["script"]!.GetValue<string>().Should().Be("Restart-Service Spooler"); // changed
        node["data"]!["config"]!["timeoutSeconds"]!.GetValue<int>().Should().Be(60);   // preserved
    }

    [Fact]
    public void Merge_EdgeHandlesAndConditionExpression_Preserved_WhenAiOmitsThem()
    {
        var original = Parse("""
            {
              "nodes": [
                { "id": "a", "type": "activity", "position": {"x":0,"y":0}, "data": { "activityType": "manualTrigger", "config": {} } },
                { "id": "b", "type": "activity", "position": {"x":300,"y":0}, "data": { "activityType": "log", "config": {} } }
              ],
              "edges": [
                { "id": "e1", "source": "a", "target": "b", "type": "labeled",
                  "sourceHandle": "out-1", "targetHandle": "in-1",
                  "data": { "label": "go", "conditionExpression": { "op": "isTrue" } } }
              ]
            }
            """);
        var proposed = Parse("""
            {
              "nodes": [
                { "id": "a", "type": "activity", "position": {"x":0,"y":0}, "data": { "activityType": "manualTrigger", "config": {} } },
                { "id": "b", "type": "activity", "position": {"x":300,"y":0}, "data": { "activityType": "log", "config": {} } }
              ],
              "edges": [
                { "id": "e1", "source": "a", "target": "b", "type": "labeled",
                  "data": { "label": "renamed" } }
              ]
            }
            """);

        var merged = WorkflowDefinitionMerge.Merge(original, proposed).Definition;
        var edge = (merged["edges"]!.AsArray())[0]!;

        edge["data"]!["label"]!.GetValue<string>().Should().Be("renamed");
        edge["sourceHandle"]!.GetValue<string>().Should().Be("out-1");
        edge["targetHandle"]!.GetValue<string>().Should().Be("in-1");
        edge["data"]!["conditionExpression"]!["op"]!.GetValue<string>().Should().Be("isTrue");
    }

    [Fact]
    public void Merge_DroppedNode_IsDeleted_NewNode_IsAdded()
    {
        var original = Parse("""
            { "nodes": [ { "id": "keep", "type": "activity", "position": {"x":0,"y":0}, "data": { "activityType": "manualTrigger", "config": {} } },
                          { "id": "gone", "type": "activity", "position": {"x":1,"y":1}, "data": { "activityType": "log", "config": {} } } ],
              "edges": [] }
            """);
        var proposed = Parse("""
            { "nodes": [ { "id": "keep", "type": "activity", "position": {"x":0,"y":0}, "data": { "activityType": "manualTrigger", "config": {} } },
                          { "id": "fresh", "type": "activity", "position": {"x":5,"y":5}, "data": { "activityType": "log", "config": {} } } ],
              "edges": [] }
            """);

        var merged = WorkflowDefinitionMerge.Merge(original, proposed).Definition;
        var ids = merged["nodes"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()).ToList();

        ids.Should().BeEquivalentTo(new[] { "keep", "fresh" });
    }

    // ---- Secret rules ---------------------------------------------------------------

    [Fact]
    public void Merge_MaskedSecret_RestoredFromOriginal_NoNote()
    {
        var original = Parse("""
            { "nodes": [ { "id": "n1", "type": "activity", "position": {"x":0,"y":0},
                "data": { "activityType": "restApi", "config": { "url": "https://x", "apiKey": "REAL-KEY" } } } ], "edges": [] }
            """);
        // The AI only ever saw the redacted JSON, so apiKey comes back as "***".
        var proposed = Parse("""
            { "nodes": [ { "id": "n1", "type": "activity", "position": {"x":0,"y":0},
                "data": { "activityType": "restApi", "config": { "url": "https://y", "apiKey": "***" } } } ], "edges": [] }
            """);

        var result = WorkflowDefinitionMerge.Merge(original, proposed);
        var node = (result.Definition["nodes"]!.AsArray())[0]!;

        node["data"]!["config"]!["url"]!.GetValue<string>().Should().Be("https://y");      // changed
        node["data"]!["config"]!["apiKey"]!.GetValue<string>().Should().Be("REAL-KEY");    // restored from the original
        result.Notes.Should().BeEmpty();
    }

    [Fact]
    public void Merge_AiDivergentSecret_RestoresOriginal_AndNotes()
    {
        var original = Parse("""
            { "nodes": [ { "id": "n1", "type": "activity", "position": {"x":0,"y":0},
                "data": { "activityType": "restApi", "config": { "apiKey": "REAL-KEY" } } } ], "edges": [] }
            """);
        var proposed = Parse("""
            { "nodes": [ { "id": "n1", "type": "activity", "position": {"x":0,"y":0},
                "data": { "activityType": "restApi", "config": { "apiKey": "hacked-by-ai" } } } ], "edges": [] }
            """);

        var result = WorkflowDefinitionMerge.Merge(original, proposed);
        var node = (result.Definition["nodes"]!.AsArray())[0]!;

        node["data"]!["config"]!["apiKey"]!.GetValue<string>().Should().Be("REAL-KEY");
        result.Notes.Should().ContainSingle().Which.Should().Contain("apiKey");
    }

    [Fact]
    public void Merge_AiInventedSecret_OnNewNode_MaskedAndNoted()
    {
        var original = Parse("""{ "nodes": [], "edges": [] }""");
        var proposed = Parse("""
            { "nodes": [ { "id": "new", "type": "activity", "position": {"x":0,"y":0},
                "data": { "activityType": "restApi", "config": { "password": "invented" } } } ], "edges": [] }
            """);

        var result = WorkflowDefinitionMerge.Merge(original, proposed);
        var node = (result.Definition["nodes"]!.AsArray())[0]!;

        node["data"]!["config"]!["password"]!.GetValue<string>().Should().Be("***");
        result.Notes.Should().ContainSingle().Which.Should().Contain("password");
    }

    [Fact]
    public void Merge_ContentMaskedHeadersString_RestoredFromOriginal_ByUniversalMaskRule()
    {
        // `headers` is NOT a secret key — the inline secret is caught by content detection and the
        // whole value is masked to "***". The AI only ever saw "***", so a naive write would corrupt
        // the real header; the universal "***"-restore rule must bring the original back.
        var original = Parse("""
            { "nodes": [ { "id": "n1", "type": "activity", "position": {"x":0,"y":0},
                "data": { "activityType": "restApi",
                          "config": { "url": "https://x", "headers": "Authorization: Bearer sk-live-REAL123" } } } ], "edges": [] }
            """);
        var proposed = Parse("""
            { "nodes": [ { "id": "n1", "type": "activity", "position": {"x":0,"y":0},
                "data": { "activityType": "restApi",
                          "config": { "url": "https://y", "headers": "***" } } } ], "edges": [] }
            """);

        var result = WorkflowDefinitionMerge.Merge(original, proposed);
        var cfg = (result.Definition["nodes"]!.AsArray())[0]!["data"]!["config"]!;

        cfg["url"]!.GetValue<string>().Should().Be("https://y");                                   // changed
        cfg["headers"]!.GetValue<string>().Should().Be("Authorization: Bearer sk-live-REAL123");   // restored
        result.Notes.Should().BeEmpty();
    }
}
