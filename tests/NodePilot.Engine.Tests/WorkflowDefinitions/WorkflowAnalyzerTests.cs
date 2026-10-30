using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Engine.Tests.WorkflowDefinitions;

/// <summary>
/// Behaviour of the single <c>analyze_workflow</c> analyzer shared by the MCP tools and the AI
/// chat. The frontend-mirror codes are pinned separately in
/// <see cref="WorkflowAnalyzerFrontendParityTests"/>; this file covers the graph semantics and the
/// three points where the two former copies disagreed before they were merged.
/// </summary>
public class WorkflowAnalyzerTests
{
    [Fact]
    public void LongLinearGraph_IsAnalyzedWithoutRecursiveTraversal()
    {
        const int count = 1500;
        var json = JsonSerializer.Serialize(new
        {
            nodes = Enumerable.Range(0, count).Select(i => new
            {
                id = "n" + i, type = "activity",
                data = new { activityType = i == 0 ? "manualTrigger" : "log", config = new { } }
            }),
            edges = Enumerable.Range(1, count - 1).Select(i => new { id = "e" + i, source = "n" + (i - 1), target = "n" + i })
        });
        var result = Analyze(json);
        result.NodeCount.Should().Be(count);
        result.Findings.Should().NotContain(f => f.Code == "cycle");
    }

    [Fact]
    public void DisabledDuplicateAlias_DoesNotReplaceActiveProducer()
    {
        var definition = WorkflowDefinitionDocument.Parse("""
            {"nodes":[
              {"id":"active","type":"activity","data":{"activityType":"manualTrigger","outputVariable":"value","config":{}}},
              {"id":"disabled","type":"activity","data":{"activityType":"log","outputVariable":"value","disabled":true,"config":{}}}
            ],"edges":[]}
            """);
        definition.OutputVariableToStepId["value"].Should().Be("active");
    }

    [Fact]
    public void DuplicateActiveAliases_AreRejectedCaseInsensitively()
    {
        using var json = JsonDocument.Parse("""
            {"nodes":[
              {"id":"one","type":"activity","data":{"activityType":"manualTrigger","outputVariable":"Value","config":{}}},
              {"id":"two","type":"activity","data":{"activityType":"log","outputVariable":"value","config":{}}}
            ],"edges":[]}
            """);
        var result = WorkflowDefinitionStructuralValidator.Validate(json.RootElement);
        result.IsValid.Should().BeFalse();
    }

    private static WorkflowAnalyzer.AnalysisResult Analyze(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return WorkflowAnalyzer.Analyze(doc.RootElement);
    }

    [Fact]
    public void Analyze_CleanLinearWorkflow_NoFindings()
    {
        var r = Analyze("""
            {"nodes":[
              {"id":"t1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"scheduleTrigger","config":{}}},
              {"id":"s1","type":"activity","position":{"x":300,"y":0},"data":{"activityType":"log","config":{"message":"hi"}}}
            ],"edges":[{"id":"e1","source":"t1","target":"s1","type":"labeled","data":{}}]}
            """);

        r.Findings.Should().BeEmpty();
        r.Ok.Should().BeTrue();
        r.NodeCount.Should().Be(2);
        r.EdgeCount.Should().Be(1);
        r.Roots.Should().ContainSingle().Which.Should().Be("t1");
    }

    [Fact]
    public void Analyze_NoTrigger_ReportsNoTriggerError()
    {
        var r = Analyze("""{"nodes":[{"id":"s1","type":"activity","data":{"activityType":"log","config":{}}}],"edges":[]}""");

        r.Findings.Should().Contain(f => f.Code == "no-trigger" && f.Severity == "error");
        r.Ok.Should().BeFalse();
    }

    [Fact]
    public void Analyze_UnreachableNode_ReportsItWithNodeId()
    {
        var r = Analyze("""
            {"nodes":[
              {"id":"t1","type":"activity","data":{"activityType":"scheduleTrigger","config":{}}},
              {"id":"s1","type":"activity","data":{"activityType":"log","config":{}}},
              {"id":"lonely","type":"activity","data":{"activityType":"log","config":{}}}
            ],"edges":[{"id":"e1","source":"t1","target":"s1","type":"labeled","data":{}}]}
            """);

        r.Findings.Should().Contain(f => f.Code == "unreachable-node" && f.NodeId == "lonely");
        r.Findings.Should().NotContain(f => f.NodeId == "s1"); // s1 is reachable
    }

    [Fact]
    public void Analyze_DisabledOrphan_IsNotFlagged()
    {
        var r = Analyze("""
            {"nodes":[
              {"id":"t1","type":"activity","data":{"activityType":"scheduleTrigger","config":{}}},
              {"id":"off","type":"activity","data":{"activityType":"log","config":{},"disabled":true}}
            ],"edges":[]}
            """);

        r.Findings.Should().NotContain(f => f.NodeId == "off"); // disabled = intentional
    }

    /// <summary>
    /// The engine has no inDegree fallback, so a cyclic graph Fails outright. The chat's former
    /// analyzer called this a warning and the MCP one an error; the engine's actual behaviour
    /// decides it.
    /// </summary>
    [Fact]
    public void Analyze_Cycle_IsAnError()
    {
        var r = Analyze("""
            {"nodes":[
              {"id":"t1","type":"activity","data":{"activityType":"scheduleTrigger","config":{}}},
              {"id":"join","type":"activity","data":{"activityType":"junction","config":{"mode":"waitAll"}}},
              {"id":"a","type":"activity","data":{"activityType":"log","config":{}}},
              {"id":"b","type":"activity","data":{"activityType":"log","config":{}}}
            ],"edges":[
              {"id":"e1","source":"t1","target":"join","type":"labeled","data":{}},
              {"id":"e2","source":"join","target":"a","type":"labeled","data":{}},
              {"id":"e3","source":"a","target":"b","type":"labeled","data":{}},
              {"id":"e4","source":"b","target":"join","type":"labeled","data":{}}
            ]}
            """);

        r.Findings.Should().Contain(f => f.Code == "cycle" && f.Severity == "error");
        r.Ok.Should().BeFalse();
    }

    [Fact]
    public void Analyze_RemoteWithoutMachine_ReportsMissingTargetMachine()
    {
        var r = Analyze("""
            {"nodes":[
              {"id":"t1","type":"activity","data":{"activityType":"manualTrigger","config":{}}},
              {"id":"r1","type":"activity","data":{"activityType":"fileOperation","config":{}}}
            ],"edges":[{"id":"e1","source":"t1","target":"r1","type":"labeled","data":{}}]}
            """);

        r.Findings.Should().Contain(f => f.Code == "missing-target-machine" && f.NodeId == "r1");
    }

    /// <summary>
    /// runScript, waitForCondition, xmlQuery and jsonQuery are hybrid: without a target machine
    /// they run in-process under the Localhost bypass, which is a product feature. The chat's
    /// former analyzer flagged exactly this and told authors to "fix" a correct workflow.
    /// </summary>
    [Theory]
    [InlineData("runScript")]
    [InlineData("waitForCondition")]
    [InlineData("xmlQuery")]
    [InlineData("jsonQuery")]
    public void Analyze_HybridActivityWithoutMachine_IsNotFlagged(string activityType)
    {
        var r = Analyze("""
            {"nodes":[
              {"id":"t1","type":"activity","data":{"activityType":"manualTrigger","config":{}}},
              {"id":"h1","type":"activity","data":{"activityType":"HYBRID","config":{}}}
            ],"edges":[{"id":"e1","source":"t1","target":"h1","type":"labeled","data":{}}]}
            """.Replace("HYBRID", activityType));

        r.Findings.Should().NotContain(f => f.Code == "missing-target-machine");
    }

    /// <summary>
    /// custom:&lt;key&gt; activities are user-authored and resolved at run time, so they are absent
    /// from the static catalog by design and must not read as a typo'd activity type.
    /// </summary>
    [Fact]
    public void Analyze_CustomActivity_IsNotReportedAsUnknownType()
    {
        var r = Analyze("""
            {"nodes":[
              {"id":"t1","type":"activity","data":{"activityType":"manualTrigger","config":{}}},
              {"id":"c1","type":"activity","data":{"activityType":"custom:disk-report","config":{}}}
            ],"edges":[{"id":"e1","source":"t1","target":"c1","type":"labeled","data":{}}]}
            """);

        r.Findings.Should().NotContain(f => f.Code == "unknown-activity-type");
    }

    /// <summary>
    /// An unknown activity type is caught by the structural pre-check, which names both the node
    /// index and the offending type — the analyzer deliberately no longer duplicates that check
    /// with a vaguer message of its own.
    /// </summary>
    [Fact]
    public void Analyze_UnknownActivityType_IsReportedAsInvalidStructure()
    {
        var r = Analyze("""
            {"nodes":[
              {"id":"t1","type":"activity","data":{"activityType":"manualTrigger","config":{}}},
              {"id":"x1","type":"activity","data":{"activityType":"thisDoesNotExist","config":{}}}
            ],"edges":[{"id":"e1","source":"t1","target":"x1","type":"labeled","data":{}}]}
            """);

        r.Ok.Should().BeFalse();
        r.Findings.Should().ContainSingle().Which.Code.Should().Be("invalid-structure");
        r.Findings[0].Message.Should().Contain("thisDoesNotExist");
    }

    [Fact]
    public void Analyze_DanglingEdge_ReportsInvalidStructureAndStopsEarly()
    {
        var r = Analyze("""
            {"nodes":[{"id":"t1","type":"activity","data":{"activityType":"manualTrigger","config":{}}}],
             "edges":[{"id":"e1","source":"t1","target":"ghost","type":"labeled","data":{}}]}
            """);

        r.Findings.Should().ContainSingle().Which.Code.Should().Be("invalid-structure");
        r.Ok.Should().BeFalse();
    }

    [Fact]
    public void Analyze_DirectFanIn_ReportsDedicatedJunctionFinding()
    {
        var r = Analyze("""
            {"nodes":[
              {"id":"t1","type":"activity","data":{"activityType":"manualTrigger","config":{}}},
              {"id":"a","type":"activity","data":{"activityType":"log","config":{}}},
              {"id":"target","type":"activity","data":{"activityType":"log","config":{}}}
            ],"edges":[
              {"id":"e1","source":"t1","target":"target","type":"labeled","data":{}},
              {"id":"e2","source":"a","target":"target","type":"labeled","data":{}}
            ]}
            """);

        r.Ok.Should().BeFalse();
        r.Findings.Should().ContainSingle().Which.Code.Should().Be("fan-in-requires-junction");
    }

    /// <summary>
    /// An empty workflow succeeds with zero steps; reporting "no trigger" would be incorrect.
    /// </summary>
    [Fact]
    public void Analyze_EmptyWorkflow_ReportsNothing()
    {
        var r = Analyze("""{"nodes":[],"edges":[]}""");

        r.Findings.Should().BeEmpty();
        r.Ok.Should().BeTrue();
        r.NodeCount.Should().Be(0);
    }
}
