using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace NodePilot.Ai.Tests;

public class WorkflowDefinitionMergeTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ---- Field preservation (layout / semantics) -------------------------------------

    [Fact]
    public void Merge_MaskedSecretScript_RestoredFromOriginal_NextToAReadableScript()
    {
        // The assistant saw the second script in clear and the first one masked (it holds a
        // password). Echoing the mask must bring back the original, editing the clear one sticks.
        var original = Parse("""
            { "nodes": [
                { "id": "s1", "type": "activity", "data": { "activityType": "runScript",
                  "config": { "script": "$pw = 'abc'; Connect-Box -Password $pw" } } },
                { "id": "s2", "type": "activity", "data": { "activityType": "runScript",
                  "config": { "script": "Get-PSDrive C" } } }
              ], "edges": [] }
            """);
        var proposed = Parse("""
            { "nodes": [
                { "id": "s1", "type": "activity", "data": { "activityType": "runScript", "config": { "script": "***" } } },
                { "id": "s2", "type": "activity", "data": { "activityType": "runScript", "config": { "script": "Get-PSDrive D" } } }
              ], "edges": [] }
            """);

        var nodes = WorkflowDefinitionMerge.Merge(original, proposed).Definition["nodes"]!.AsArray();

        nodes[0]!["data"]!["config"]!["script"]!.GetValue<string>().Should().Be("$pw = 'abc'; Connect-Box -Password $pw");
        nodes[1]!["data"]!["config"]!["script"]!.GetValue<string>().Should().Be("Get-PSDrive D");
    }

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
        // The AI only changes the label, omitting position/credentialId/the remaining config
        // fields.
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

    [Theory]
    [InlineData("4711")]
    [InlineData("""{ "value": "REAL-KEY", "rotation": 30 }""")]
    [InlineData("""[ "REAL-KEY", 2 ]""")]
    public void Merge_RedactedNonStringSecret_RoundTripRestoresOriginal(string rawSecret)
    {
        // The AI sees the shared redactor's output and hands it back unchanged.
        var original = Parse($$"""
            { "nodes": [ { "id": "n1", "type": "activity", "position": {"x":0,"y":0},
                "data": { "activityType": "restApi", "config": { "token": {{rawSecret}}, "method": "GET" } } } ], "edges": [] }
            """);
        var redacted = NodePilot.Core.WorkflowDefinitions.WorkflowSecretRedactor.Redact(original);
        redacted["nodes"]![0]!["data"]!["config"]!["token"]!.GetValue<string>().Should().Be("***");

        var result = WorkflowDefinitionMerge.Merge(original, Parse(redacted.ToJsonString()));
        var token = result.Definition["nodes"]![0]!["data"]!["config"]!["token"];

        System.Text.Json.Nodes.JsonNode.DeepEquals(token, System.Text.Json.Nodes.JsonNode.Parse(rawSecret))
            .Should().BeTrue("the merge must restore the exact original value, got {0}", token?.ToJsonString());
        result.Notes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("4711", "9999")]
    [InlineData("""{ "value": "REAL-KEY" }""", """{ "value": "from-ai" }""")]
    [InlineData("""[ "REAL-KEY" ]""", """[ "from-ai" ]""")]
    public void Merge_AiDivergentNonStringSecret_RestoresOriginal_AndNotes(string rawOriginal, string rawProposed)
    {
        var original = Parse($$"""
            { "nodes": [ { "id": "n1", "type": "activity", "position": {"x":0,"y":0},
                "data": { "activityType": "restApi", "config": { "password": {{rawOriginal}} } } } ], "edges": [] }
            """);
        var proposed = Parse($$"""
            { "nodes": [ { "id": "n1", "type": "activity", "position": {"x":0,"y":0},
                "data": { "activityType": "restApi", "config": { "password": {{rawProposed}} } } } ], "edges": [] }
            """);

        var result = WorkflowDefinitionMerge.Merge(original, proposed);
        var password = result.Definition["nodes"]![0]!["data"]!["config"]!["password"];

        System.Text.Json.Nodes.JsonNode.DeepEquals(password, System.Text.Json.Nodes.JsonNode.Parse(rawOriginal))
            .Should().BeTrue();
        result.Notes.Should().ContainSingle().Which.Should().Contain("password");
    }

    [Fact]
    public void Merge_AiInventedNonStringSecret_MaskedAndNoted()
    {
        var original = Parse("""{ "nodes": [], "edges": [] }""");
        var proposed = Parse("""
            { "nodes": [ { "id": "new", "type": "activity", "position": {"x":0,"y":0},
                "data": { "activityType": "restApi", "config": { "apiKey": { "value": "invented" } } } } ], "edges": [] }
            """);

        var result = WorkflowDefinitionMerge.Merge(original, proposed);

        result.Definition["nodes"]![0]!["data"]!["config"]!["apiKey"]!.GetValue<string>().Should().Be("***");
        result.Notes.Should().ContainSingle().Which.Should().Contain("apiKey");
    }

    [Fact]
    public void Merge_ContentMaskedHeadersString_RestoredFromOriginal_ByUniversalMaskRule()
    {
        // `headers` is not a secret key, and the inline secret is caught by content detection: the
        // whole value is masked to "*". The AI only ever saw "*", so a naive write would
        // corrupt the real header; the universal "***"-restore rule brings the original back.
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

    [Fact]
    public void Merge_OpaqueHeadersObjectMask_RestoresCompleteOriginalShape()
    {
        var original = Parse("""
            { "nodes": [ { "id": "n1", "type": "activity", "position": {"x":0,"y":0},
                "data": { "activityType": "restApi", "config": {
                    "url": "https://x", "headers": { "Accept": "application/json", "X-Legacy": "opaque-value" }
                } } } ], "edges": [] }
            """);
        var proposed = Parse("""
            { "nodes": [ { "id": "n1", "type": "activity", "position": {"x":0,"y":0},
                "data": { "activityType": "restApi", "config": { "url": "https://y", "headers": "***" } } } ], "edges": [] }
            """);

        var result = WorkflowDefinitionMerge.Merge(original, proposed);
        var cfg = result.Definition["nodes"]![0]!["data"]!["config"]!;

        cfg["url"]!.GetValue<string>().Should().Be("https://y");
        cfg["headers"]!["Accept"]!.GetValue<string>().Should().Be("application/json");
        cfg["headers"]!["X-Legacy"]!.GetValue<string>().Should().Be("opaque-value");
    }

    [Fact]
    public void Merge_MaskedLiteralsInsideNestedArrays_RestoresOriginalConditionsAndCases()
    {
        var original = Parse("""
            { "nodes": [{ "id": "decision-1", "type": "decision", "data": { "config": {
                "cases": [{ "label": "secret-case", "conditionExpression": {
                    "type": "comparison", "left": { "kind": "variable", "value": "x" },
                    "op": "==", "right": { "kind": "literal", "value": "case-secret" }
                }}]
            }}}], "edges": [{ "id": "edge-1", "source": "a", "target": "b", "data": {
                "conditionExpression": { "type": "group", "op": "AND", "children": [{
                    "type": "comparison", "left": { "kind": "literal", "value": "left-secret" },
                    "op": "==", "right": { "kind": "variable", "value": "y" }
                }]}
            }}] }
            """);
        var proposed = Parse("""
            { "nodes": [{ "id": "decision-1", "type": "decision", "data": { "config": {
                "cases": [{ "label": "secret-case", "conditionExpression": {
                    "type": "comparison", "left": { "kind": "variable", "value": "x" },
                    "op": "==", "right": { "kind": "literal", "value": "***" }
                }}]
            }}}], "edges": [{ "id": "edge-1", "source": "a", "target": "b", "data": {
                "conditionExpression": { "type": "group", "op": "AND", "children": [{
                    "type": "comparison", "left": { "kind": "literal", "value": "***" },
                    "op": "==", "right": { "kind": "variable", "value": "y" }
                }]}
            }}] }
            """);

        var result = WorkflowDefinitionMerge.Merge(original, proposed);
        var merged = result.Definition;

        merged["nodes"]![0]!["data"]!["config"]!["cases"]![0]!["conditionExpression"]!
            ["right"]!["value"]!.GetValue<string>().Should().Be("case-secret");
        merged["edges"]![0]!["data"]!["conditionExpression"]!["children"]![0]!
            ["left"]!["value"]!.GetValue<string>().Should().Be("left-secret");
        result.Notes.Should().BeEmpty("arrays that differ only by their masks were not changed");
    }

    private const string DecisionWithMaskedCase = """
        { "nodes": [{ "id": "decision-1", "type": "decision", "data": { "config": {
            "cases": [{ "label": "prod", "conditionExpression": {
                "type": "comparison", "left": { "kind": "variable", "value": "env" },
                "op": "==", "right": { "kind": "literal", "value": "LITERAL" }
            }}]
        }}}], "edges": [] }
        """;

    [Fact]
    public void Merge_AiAddsDecisionCase_KeepsOriginalCasesAndNotes()
    {
        var original = Parse(DecisionWithMaskedCase.Replace("LITERAL", "prod-secret"));
        var proposed = Parse("""
            { "nodes": [{ "id": "decision-1", "type": "decision", "data": { "config": {
                "cases": [
                  { "label": "prod", "conditionExpression": {
                    "type": "comparison", "left": { "kind": "variable", "value": "env" },
                    "op": "==", "right": { "kind": "literal", "value": "***" } } },
                  { "label": "test", "conditionExpression": {
                    "type": "comparison", "left": { "kind": "variable", "value": "env" },
                    "op": "==", "right": { "kind": "literal", "value": "test" } } }
                ]
            }}}], "edges": [] }
            """);

        var result = WorkflowDefinitionMerge.Merge(original, proposed);

        var cases = result.Definition["nodes"]![0]!["data"]!["config"]!["cases"]!.AsArray();
        cases.Should().ContainSingle("the masked array is restored from the original");
        cases[0]!["conditionExpression"]!["right"]!["value"]!.GetValue<string>().Should().Be("prod-secret");
        result.Notes.Should().ContainSingle()
            .Which.Should().Contain("'cases'").And.Contain("'decision-1'");
    }

    [Fact]
    public void Merge_AiChangesOperatorInMaskedConditionGroup_KeepsOriginalAndNotes()
    {
        var original = Parse("""
            { "nodes": [], "edges": [{ "id": "edge-1", "source": "a", "target": "b", "data": {
                "conditionExpression": { "type": "group", "op": "AND", "children": [{
                    "type": "comparison", "left": { "kind": "literal", "value": "left-secret" },
                    "op": "==", "right": { "kind": "variable", "value": "y" } }] } } }] }
            """);
        var proposed = Parse("""
            { "nodes": [], "edges": [{ "id": "edge-1", "source": "a", "target": "b", "data": {
                "conditionExpression": { "type": "group", "op": "AND", "children": [{
                    "type": "comparison", "left": { "kind": "literal", "value": "***" },
                    "op": "!=", "right": { "kind": "variable", "value": "y" } }] } } }] }
            """);

        var result = WorkflowDefinitionMerge.Merge(original, proposed);

        var child = result.Definition["edges"]![0]!["data"]!["conditionExpression"]!["children"]![0]!;
        child["op"]!.GetValue<string>().Should().Be("==");
        child["left"]!["value"]!.GetValue<string>().Should().Be("left-secret");
        result.Notes.Should().ContainSingle()
            .Which.Should().Contain("'children'").And.Contain("'edge-1'");
    }

    [Fact]
    public void Merge_MaskedCaseWithOmittedKeys_AddsNoNote()
    {
        var original = Parse(DecisionWithMaskedCase.Replace("LITERAL", "prod-secret"));
        // The AI left out the label; omitted keys mean "unchanged", as everywhere else in the merge.
        var proposed = Parse("""
            { "nodes": [{ "id": "decision-1", "type": "decision", "data": { "config": {
                "cases": [{ "conditionExpression": {
                    "type": "comparison", "left": { "kind": "variable", "value": "env" },
                    "op": "==", "right": { "kind": "literal", "value": "***" } } }]
            }}}], "edges": [] }
            """);

        var result = WorkflowDefinitionMerge.Merge(original, proposed);

        result.Definition["nodes"]![0]!["data"]!["config"]!["cases"]![0]!["label"]!
            .GetValue<string>().Should().Be("prod");
        result.Notes.Should().BeEmpty();
    }
}
