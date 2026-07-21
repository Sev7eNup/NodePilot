using FluentAssertions;
using NodePilot.Core.Models;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Engine.Tests.WorkflowDefinitions;

public class WorkflowDefinitionDocumentTests
{
    [Fact]
    public void Parse_CentralizesDisabledRuntimeSemanticsAndMetadata()
    {
        var definition = WorkflowDefinitionDocument.Parse("""
        {
          "nodes": [
            { "id": "manual", "type": "activity", "data": { "activityType": "manualTrigger", "config": {} } },
            { "id": "disabledSchedule", "type": "activity", "data": { "activityType": "scheduleTrigger", "disabled": true, "config": { "cronExpression": "0 0/5 * * * ?" } } },
            { "id": "work", "type": "activity", "data": { "activityType": "runScript", "outputVariable": "scriptOut", "config": {} } },
            { "id": "disabledWork", "type": "activity", "data": { "activityType": "log", "disabled": true, "config": {} } }
          ],
          "edges": [
            { "id": "e1", "source": "manual", "target": "work", "data": {} },
            { "id": "e2", "source": "work", "target": "disabledWork", "data": {} }
          ]
        }
        """);

        definition.RootNodes.Should().ContainSingle(n => n.Id == "manual");
        definition.ActiveEdges.Should().ContainSingle(e => e.Id == "e1");
        definition.OutputVariableToStepId.Should().ContainKey("scriptOut").WhoseValue.Should().Be("work");
        definition.TriggerDescriptors.Should().ContainSingle(d => d.ActivityType == "manualTrigger");
        definition.Metadata.ActivityCount.Should().Be(1);
        definition.Metadata.TriggerTypes.Should().Equal("manualTrigger");
    }

    [Fact]
    public void Parse_TriggerlessGraph_HasNoRootNodes()
    {
        // Roots are trigger-ONLY: a graph of plain activities (no trigger node) has NO root,
        // regardless of in-degree. There is intentionally no inDegree-0 fallback, so such a
        // workflow can never start a node (the engine fails it). This pins the core semantic.
        var definition = WorkflowDefinitionDocument.Parse("""
        {
          "nodes": [
            { "id": "step-1", "type": "activity", "data": { "activityType": "runScript", "config": {} } },
            { "id": "step-2", "type": "activity", "data": { "activityType": "runScript", "config": {} } }
          ],
          "edges": [
            { "id": "e1", "source": "step-1", "target": "step-2", "data": {} }
          ]
        }
        """);

        definition.RootNodes.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EnabledNonManualTrigger_IsARoot()
    {
        // Roots are EVERY (enabled) trigger type — not just manualTrigger. A schedule/webhook/…
        // trigger is just as valid an entry point.
        var definition = WorkflowDefinitionDocument.Parse("""
        {
          "nodes": [
            { "id": "sched", "type": "activity", "data": { "activityType": "scheduleTrigger", "config": { "cronExpression": "0 0 * * * ?" } } },
            { "id": "work", "type": "activity", "data": { "activityType": "runScript", "config": {} } }
          ],
          "edges": [ { "id": "e1", "source": "sched", "target": "work", "data": {} } ]
        }
        """);

        definition.RootNodes.Should().ContainSingle(n => n.Id == "sched");
    }

    [Fact]
    public void Parse_MultipleEnabledTriggers_AreAllRoots_DisabledExcluded()
    {
        // Every enabled trigger node is a root (manual + schedule here); a disabled trigger is not.
        var definition = WorkflowDefinitionDocument.Parse("""
        {
          "nodes": [
            { "id": "manual", "type": "activity", "data": { "activityType": "manualTrigger", "config": {} } },
            { "id": "sched", "type": "activity", "data": { "activityType": "scheduleTrigger", "config": { "cronExpression": "0 0 * * * ?" } } },
            { "id": "hookOff", "type": "activity", "data": { "activityType": "webhookTrigger", "disabled": true, "config": { "path": "x" } } },
            { "id": "work", "type": "activity", "data": { "activityType": "runScript", "config": {} } }
          ],
          "edges": [
            { "id": "e1", "source": "manual", "target": "work", "data": {} },
            { "id": "e2", "source": "sched", "target": "work", "data": {} }
          ]
        }
        """);

        definition.RootNodes.Select(n => n.Id).Should().BeEquivalentTo(new[] { "manual", "sched" });
    }

    [Fact]
    public void FindFirstTrigger_IgnoresDisabledTriggersAndReturnsConfig()
    {
        var definition = WorkflowDefinitionDocument.Parse("""
        {
          "nodes": [
            { "id": "disabledWebhook", "type": "activity", "data": { "activityType": "webhookTrigger", "disabled": true, "config": { "path": "disabled" } } },
            { "id": "enabledWebhook", "type": "activity", "data": { "activityType": "webhookTrigger", "config": { "path": "hook" } } }
          ],
          "edges": []
        }
        """);

        var trigger = definition.FindFirstTrigger("webhookTrigger");

        trigger.Should().NotBeNull();
        trigger!.NodeId.Should().Be("enabledWebhook");
        trigger.Config.GetProperty("path").GetString().Should().Be("hook");
    }

    [Fact]
    public void FindAncestorNodeIds_UsesActiveEdgesByDefaultAndCanIncludeDisabledEdges()
    {
        var definition = WorkflowDefinitionDocument.Parse("""
        {
          "nodes": [
            { "id": "a", "type": "activity", "data": { "activityType": "runScript", "config": {} } },
            { "id": "b", "type": "activity", "data": { "activityType": "runScript", "config": {} } },
            { "id": "c", "type": "activity", "data": { "activityType": "runScript", "config": {} } }
          ],
          "edges": [
            { "id": "a-b", "source": "a", "target": "b", "data": { "disabled": true } },
            { "id": "b-c", "source": "b", "target": "c", "data": {} }
          ]
        }
        """);

        definition.FindAncestorNodeIds("c").Should().BeEquivalentTo("b");
        definition.FindAncestorNodeIds("c", includeDisabledEdges: true).Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public void WorkflowMetadata_UsesDocumentSemantics()
    {
        var (activityCount, triggerTypesJson) = WorkflowMetadata.Compute("""
        {
          "nodes": [
            { "id": "a", "data": { "activityType": "scheduleTrigger", "disabled": true } },
            { "id": "b", "data": { "activityType": "webhookTrigger" } },
            { "id": "c", "data": { "activityType": "log" } },
            { "id": "d", "data": { "activityType": "runScript", "disabled": true } }
          ],
          "edges": []
        }
        """);

        activityCount.Should().Be(1);
        triggerTypesJson.Should().Be("""["webhookTrigger"]""");
    }
}
