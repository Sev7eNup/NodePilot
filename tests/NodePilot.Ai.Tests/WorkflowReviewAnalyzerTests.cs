using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Ai.Tests;

public class WorkflowReviewAnalyzerTests
{
    private static IReadOnlyList<ReviewFinding> Analyze(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return WorkflowReviewAnalyzer.Analyze(doc.RootElement);
    }

    [Fact]
    public void Analyze_CleanLinearWorkflow_NoFindings()
    {
        var f = Analyze("""
            {"nodes":[
              {"id":"t1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"scheduleTrigger","config":{}}},
              {"id":"s1","type":"activity","position":{"x":300,"y":0},"data":{"activityType":"log","config":{"message":"hi"}}}
            ],"edges":[{"id":"e1","source":"t1","target":"s1","type":"labeled","data":{}}]}
            """);
        f.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_NoTrigger_ReportsNoTriggerError()
    {
        var f = Analyze("""{"nodes":[{"id":"s1","type":"activity","data":{"activityType":"log","config":{}}}],"edges":[]}""");
        f.Should().Contain(x => x.Code == "no-trigger" && x.Severity == ReviewSeverity.Error);
    }

    [Fact]
    public void Analyze_UnreachableNode_ReportsOrphanWithNodeId()
    {
        var f = Analyze("""
            {"nodes":[
              {"id":"t1","type":"activity","data":{"activityType":"scheduleTrigger","config":{}}},
              {"id":"s1","type":"activity","data":{"activityType":"log","config":{}}},
              {"id":"lonely","type":"activity","data":{"activityType":"log","config":{}}}
            ],"edges":[{"id":"e1","source":"t1","target":"s1","type":"labeled","data":{}}]}
            """);
        f.Should().Contain(x => x.Code == "orphan-node" && x.NodeId == "lonely");
        f.Should().NotContain(x => x.NodeId == "s1"); // s1 is reachable
    }

    [Fact]
    public void Analyze_Cycle_ReportsCycle()
    {
        var f = Analyze("""
            {"nodes":[
              {"id":"t1","type":"activity","data":{"activityType":"scheduleTrigger","config":{}}},
              {"id":"a","type":"activity","data":{"activityType":"log","config":{}}},
              {"id":"b","type":"activity","data":{"activityType":"log","config":{}}}
            ],"edges":[
              {"id":"e1","source":"t1","target":"a","type":"labeled","data":{}},
              {"id":"e2","source":"a","target":"b","type":"labeled","data":{}},
              {"id":"e3","source":"b","target":"a","type":"labeled","data":{}}
            ]}
            """);
        f.Should().Contain(x => x.Code == "cycle");
    }

    [Fact]
    public void Analyze_RemoteWithoutMachine_ReportsMissingTargetMachine()
    {
        var f = Analyze("""
            {"nodes":[
              {"id":"t1","type":"activity","data":{"activityType":"manualTrigger","config":{}}},
              {"id":"r1","type":"activity","data":{"activityType":"fileOperation","config":{}}}
            ],"edges":[{"id":"e1","source":"t1","target":"r1","type":"labeled","data":{}}]}
            """);
        f.Should().Contain(x => x.Code == "missing-target-machine" && x.NodeId == "r1");
    }

    [Fact]
    public void Analyze_DanglingEdge_ReportsInvalidStructureAndStopsEarly()
    {
        var f = Analyze("""
            {"nodes":[{"id":"t1","type":"activity","data":{"activityType":"manualTrigger","config":{}}}],
             "edges":[{"id":"e1","source":"t1","target":"ghost","type":"labeled","data":{}}]}
            """);
        f.Should().ContainSingle().Which.Code.Should().Be("invalid-structure");
    }

    [Fact]
    public void Analyze_DisabledOrphan_IsNotFlagged()
    {
        var f = Analyze("""
            {"nodes":[
              {"id":"t1","type":"activity","data":{"activityType":"scheduleTrigger","config":{}}},
              {"id":"off","type":"activity","data":{"activityType":"log","config":{},"disabled":true}}
            ],"edges":[]}
            """);
        f.Should().NotContain(x => x.NodeId == "off"); // disabled = intentional, no orphan finding
    }
}
