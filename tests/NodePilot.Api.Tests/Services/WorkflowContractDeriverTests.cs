using System.Text.Json;
using FluentAssertions;
using NodePilot.Api.Services;
using NodePilot.Core.Models;
using Xunit;

namespace NodePilot.Api.Tests.Services;

/// <summary>
/// Pins these behaviors:
/// - Manual trigger + returnData → full contract with all 4 system outputs + user outputs
/// - Only a manualTrigger → HasReturnData=false, only system outputs in Outputs[]
/// - No manualTrigger → HasManualTrigger=false, empty Inputs (workflow is still callable)
/// - Multiple returnData nodes with overlapping keys → one entry per key, Source="multiple"
/// - Malformed JSON → empty contract, no throw, system outputs are still present
/// - manualTrigger without a parameters array → empty Inputs
/// - Parameter without a type → defaults to "string"
/// - Disabled manualTrigger / returnData nodes are ignored
/// - Multiple manualTriggers are deduplicated by name
/// - Multiple manualTriggers with diverging Type/Default → HasConflict=true (Variant B)
/// - Required is OR'd across all triggers (conservative)
/// - Reserved __keys are silently stripped from user-supplied returnData
/// - Default coercion: bool/number → string, null → null, missing → null
/// - Required+Default is not a conflict — both can coexist
/// </summary>
public class WorkflowContractDeriverTests
{
    private static Workflow Wf(string defJson, string name = "wf")
        => new() { Id = Guid.NewGuid(), Name = name, DefinitionJson = defJson };

    private readonly IWorkflowContractDeriver _deriver = new WorkflowContractDeriver();

    [Fact]
    public void Derive_ManualTriggerAndReturnData_ProducesFullContract()
    {
        var workflow = Wf("""
        {
          "nodes": [
            {"id":"t","type":"trigger","data":{"activityType":"manualTrigger","config":{
              "parameters":[
                {"name":"serverName","type":"string","required":true,"description":"Target host"},
                {"name":"reboot","type":"boolean","required":false,"default":"false"}
              ]
            }}},
            {"id":"r","type":"activity","data":{"activityType":"returnData","config":{
              "data":{"patched":"{{step.success}}","summary":"OK"}
            }}}
          ],
          "edges": []
        }
        """);

        var c = _deriver.Derive(workflow);

        c.HasManualTrigger.Should().BeTrue();
        c.HasReturnData.Should().BeTrue();
        c.HasMultipleReturnDataNodes.Should().BeFalse();
        c.Inputs.Should().HaveCount(2);
        c.Inputs.Should().Contain(i => i.Name == "serverName" && i.Type == "string"
            && i.Required && i.Description == "Target host" && !i.HasConflict);
        c.Inputs.Should().Contain(i => i.Name == "reboot" && i.Type == "boolean"
            && !i.Required && i.Default == "false");
        // 4 system + 2 user = 6 outputs
        c.Outputs.Should().HaveCount(6);
        c.Outputs.Where(o => o.Source == "system").Should().HaveCount(4);
        c.Outputs.Should().Contain(o => o.Name == "patched" && o.Source == "single");
        c.Outputs.Should().Contain(o => o.Name == "summary" && o.Source == "single");
        c.Outputs.Should().Contain(o => o.Name == "__executionId" && o.Source == "system");
    }

    [Fact]
    public void Derive_OnlyManualTrigger_NoReturnData_StillEmitsSystemOutputs()
    {
        var workflow = Wf("""
        {"nodes":[
          {"id":"t","data":{"activityType":"manualTrigger","config":{"parameters":[
            {"name":"x","type":"string"}
          ]}}}
        ],"edges":[]}
        """);

        var c = _deriver.Derive(workflow);

        c.HasManualTrigger.Should().BeTrue();
        c.HasReturnData.Should().BeFalse();
        c.HasMultipleReturnDataNodes.Should().BeFalse();
        c.Outputs.Should().HaveCount(4);
        c.Outputs.Should().AllSatisfy(o => o.Source.Should().Be("system"));
    }

    [Fact]
    public void Derive_NoManualTrigger_StillCallableButNoDeclaredInputs()
    {
        // A workflow without a manualTrigger (e.g. only a scheduleTrigger, or only activities)
        // is still callable via startWorkflow — we just report an empty Inputs contract.
        var workflow = Wf("""
        {"nodes":[
          {"id":"a","data":{"activityType":"runScript","config":{}}}
        ],"edges":[]}
        """);

        var c = _deriver.Derive(workflow);

        c.HasManualTrigger.Should().BeFalse();
        c.HasReturnData.Should().BeFalse();
        c.Inputs.Should().BeEmpty();
        c.Outputs.Should().AllSatisfy(o => o.Source.Should().Be("system"));
    }

    [Fact]
    public void Derive_MultipleReturnDataNodes_KeysSurfacedWithMultipleMarker()
    {
        var workflow = Wf("""
        {"nodes":[
          {"id":"t","data":{"activityType":"manualTrigger","config":{}}},
          {"id":"r1","data":{"activityType":"returnData","config":{"data":{"foo":"a","shared":"x"}}}},
          {"id":"r2","data":{"activityType":"returnData","config":{"data":{"bar":"b","shared":"y"}}}}
        ],"edges":[]}
        """);

        var c = _deriver.Derive(workflow);

        c.HasReturnData.Should().BeTrue();
        c.HasMultipleReturnDataNodes.Should().BeTrue();
        var userOuts = c.Outputs.Where(o => o.Source == "multiple").ToList();
        userOuts.Should().HaveCount(3);  // foo, bar, shared
        userOuts.Select(o => o.Name).Should().BeEquivalentTo("foo", "bar", "shared");
    }

    [Fact]
    public void Derive_MalformedJson_ReturnsEmptyContractWithSystemOutputs()
    {
        var workflow = Wf("{ this is not json");
        var c = _deriver.Derive(workflow);

        c.HasManualTrigger.Should().BeFalse();
        c.HasReturnData.Should().BeFalse();
        c.Inputs.Should().BeEmpty();
        c.Outputs.Should().HaveCount(4);  // System outputs are always present
    }

    [Fact]
    public void Derive_ManualTriggerWithoutParametersArray_EmitsEmptyInputs()
    {
        var workflow = Wf("""
        {"nodes":[
          {"id":"t","data":{"activityType":"manualTrigger","config":{}}}
        ],"edges":[]}
        """);

        var c = _deriver.Derive(workflow);
        c.HasManualTrigger.Should().BeTrue();
        c.Inputs.Should().BeEmpty();
    }

    [Fact]
    public void Derive_ParameterWithoutType_DefaultsToString()
    {
        var workflow = Wf("""
        {"nodes":[
          {"id":"t","data":{"activityType":"manualTrigger","config":{"parameters":[
            {"name":"foo","required":true}
          ]}}}
        ],"edges":[]}
        """);

        var c = _deriver.Derive(workflow);
        c.Inputs.Single().Type.Should().Be("string");
    }

    [Fact]
    public void Derive_DisabledManualTrigger_IsIgnored()
    {
        var workflow = Wf("""
        {"nodes":[
          {"id":"t","data":{"activityType":"manualTrigger","disabled":true,"config":{"parameters":[
            {"name":"foo","type":"string"}
          ]}}}
        ],"edges":[]}
        """);

        var c = _deriver.Derive(workflow);
        c.HasManualTrigger.Should().BeFalse();
        c.Inputs.Should().BeEmpty();
    }

    [Fact]
    public void Derive_DisabledReturnData_IsIgnored()
    {
        var workflow = Wf("""
        {"nodes":[
          {"id":"r","data":{"activityType":"returnData","disabled":true,"config":{"data":{"foo":"x"}}}}
        ],"edges":[]}
        """);

        var c = _deriver.Derive(workflow);
        c.HasReturnData.Should().BeFalse();
        c.Outputs.Should().AllSatisfy(o => o.Source.Should().Be("system"));
    }

    [Fact]
    public void Derive_MultipleManualTriggers_DeduplicatesByName()
    {
        var workflow = Wf("""
        {"nodes":[
          {"id":"t1","data":{"activityType":"manualTrigger","config":{"parameters":[
            {"name":"server","type":"string","required":true},
            {"name":"reboot","type":"boolean"}
          ]}}},
          {"id":"t2","data":{"activityType":"manualTrigger","config":{"parameters":[
            {"name":"server","type":"string","required":false},
            {"name":"timeout","type":"int","default":"60"}
          ]}}}
        ],"edges":[]}
        """);

        var c = _deriver.Derive(workflow);
        c.Inputs.Should().HaveCount(3);  // server (deduped), reboot, timeout
        var server = c.Inputs.Single(i => i.Name == "server");
        // Required = OR over all declarations (conservative — required if anyone says so)
        server.Required.Should().BeTrue();
        server.HasConflict.Should().BeFalse();  // type+default identical
    }

    [Fact]
    public void Derive_DivergingTypeAcrossTriggers_SetsHasConflict()
    {
        var workflow = Wf("""
        {"nodes":[
          {"id":"t1","data":{"activityType":"manualTrigger","config":{"parameters":[
            {"name":"x","type":"string"}
          ]}}},
          {"id":"t2","data":{"activityType":"manualTrigger","config":{"parameters":[
            {"name":"x","type":"int"}
          ]}}}
        ],"edges":[]}
        """);

        var c = _deriver.Derive(workflow);
        var x = c.Inputs.Single(i => i.Name == "x");
        x.HasConflict.Should().BeTrue();
        // First-encountered values win — t1's "string" stays
        x.Type.Should().Be("string");
    }

    [Fact]
    public void Derive_DivergingDefaultAcrossTriggers_SetsHasConflict()
    {
        var workflow = Wf("""
        {"nodes":[
          {"id":"t1","data":{"activityType":"manualTrigger","config":{"parameters":[
            {"name":"x","type":"string","default":"a"}
          ]}}},
          {"id":"t2","data":{"activityType":"manualTrigger","config":{"parameters":[
            {"name":"x","type":"string","default":"b"}
          ]}}}
        ],"edges":[]}
        """);

        var c = _deriver.Derive(workflow);
        c.Inputs.Single().HasConflict.Should().BeTrue();
    }

    [Fact]
    public void Derive_ReservedKeys_FilteredFromUserReturnData()
    {
        // Author tries to declare __status as a returnData key. Engine would inject the
        // real one anyway; we silently drop the user's version so the contract stays clean.
        var workflow = Wf("""
        {"nodes":[
          {"id":"r","data":{"activityType":"returnData","config":{"data":{
            "__status":"hijacked","__executionId":"foo","myKey":"value"
          }}}}
        ],"edges":[]}
        """);

        var c = _deriver.Derive(workflow);
        c.Outputs.Where(o => o.Source != "system").Should().ContainSingle(o => o.Name == "myKey");
        // __status / __executionId only present once, with Source=system
        c.Outputs.Count(o => o.Name == "__status").Should().Be(1);
        c.Outputs.Single(o => o.Name == "__status").Source.Should().Be("system");
    }

    [Fact]
    public void Derive_DefaultCoercion_BoolNumberToString_NullToNull()
    {
        var workflow = Wf("""
        {"nodes":[
          {"id":"t","data":{"activityType":"manualTrigger","config":{"parameters":[
            {"name":"a","type":"bool","default":true},
            {"name":"b","type":"int","default":42},
            {"name":"c","type":"string","default":null},
            {"name":"d","type":"string"}
          ]}}}
        ],"edges":[]}
        """);

        var c = _deriver.Derive(workflow);
        c.Inputs.Single(i => i.Name == "a").Default.Should().Be("true");
        c.Inputs.Single(i => i.Name == "b").Default.Should().Be("42");
        c.Inputs.Single(i => i.Name == "c").Default.Should().BeNull();
        c.Inputs.Single(i => i.Name == "d").Default.Should().BeNull();
    }

    [Fact]
    public void Derive_RequiredPlusDefault_IsValidNotConflict()
    {
        // Required + Default is a meaningful combination: the parameter is declared as "must
        // be set, but if the caller omits it, fall back to the default". The deriver must not
        // flag this as a conflict; the UI validation handles that distinction separately later
        // (required+missing-with-no-default = error, required+missing-with-default = ok).
        var workflow = Wf("""
        {"nodes":[
          {"id":"t","data":{"activityType":"manualTrigger","config":{"parameters":[
            {"name":"foo","type":"string","required":true,"default":"fallback"}
          ]}}}
        ],"edges":[]}
        """);

        var c = _deriver.Derive(workflow);
        var foo = c.Inputs.Single();
        foo.Required.Should().BeTrue();
        foo.Default.Should().Be("fallback");
        foo.HasConflict.Should().BeFalse();
    }

    [Fact]
    public void Derive_EmptyDefault_PreservedNotCoercedToNull()
    {
        // An empty-string default is semantically different from "no default":
        // "" means "set it to an empty string if the caller omits it".
        var workflow = Wf("""
        {"nodes":[
          {"id":"t","data":{"activityType":"manualTrigger","config":{"parameters":[
            {"name":"x","type":"string","default":""}
          ]}}}
        ],"edges":[]}
        """);

        var c = _deriver.Derive(workflow);
        c.Inputs.Single().Default.Should().Be("");
    }
}
