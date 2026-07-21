using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Analysis;
using Xunit;

namespace NodePilot.Mcp.Tests;

/// <summary>
/// Direct unit coverage for the pure data-bus resolver (no API/WireMock). Drives the branches the
/// tool-level tests don't reach: unknown-node guard, globals/manual namespaces staying literal,
/// and the dynamic-param providers for wmiQuery + every registryOperation operation arm.
/// </summary>
public sealed class VariableResolverTests
{
    private static JsonElement E(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Available_UnknownNodeId_Throws()
    {
        var def = E("""
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"t","config":{}}}],
         "edges":[]}
        """);

        var act = () => VariableResolver.Available(def, "does-not-exist");

        act.Should().Throw<ArgumentException>().WithMessage("*does-not-exist*");
    }

    [Fact]
    public void FindUnresolved_GlobalsAndManualHeads_StayLiteral_NotFlagged()
    {
        // {{globals.X}} / {{manual.Y}} are run-level namespaces — validated at runtime, never
        // reported as unresolved even though there is no matching step name.
        var def = E("""
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"t","config":{}}},
          {"id":"use","type":"activity","data":{"activityType":"log","label":"u",
            "config":{"message":"{{globals.API_KEY}} and {{manual.env}}"}}}],
         "edges":[{"id":"e","source":"t","target":"use"}]}
        """);

        var unresolved = VariableResolver.FindUnresolved(def);

        unresolved.Should().BeEmpty();
    }

    [Fact]
    public void FindUnresolved_UnknownHeadAndBadTail_AreReportedWithCodes()
    {
        var def = E("""
        {"nodes":[
          {"id":"check","type":"activity","data":{"activityType":"runScript","label":"c",
            "config":{"script":"$x = 1"}}},
          {"id":"use","type":"activity","data":{"activityType":"log","label":"u",
            "config":{"message":"{{ghost.output}} and {{check.freeGb}} and {{check.output}} and {{check}}"}}}],
         "edges":[{"id":"e","source":"check","target":"use"}]}
        """);

        var unresolved = VariableResolver.FindUnresolved(def);

        unresolved.Should().Contain(r => r.Code == "unknown-template-ref" && r.Reference == "{{ghost.output}}");
        // invalid tail on a real step name → stays literal, with a param.X hint.
        unresolved.Should().Contain(r => r.Code == "invalid-template-tail" && r.Reference == "{{check.freeGb}}"
            && r.Reason.Contains("check.param.freeGb"));
        // no-tail on a real step name → suggests adding a tail.
        unresolved.Should().Contain(r => r.Code == "invalid-template-tail" && r.Reference == "{{check}}"
            && r.Reason.Contains("add a tail"));
        // valid tail must NOT be reported.
        unresolved.Should().NotContain(r => r.Reference == "{{check.output}}");
    }

    [Fact]
    public void Available_WmiQueryCaptureProperties_ExposeCountAndProps()
    {
        var def = E("""
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"t","config":{}}},
          {"id":"w","type":"activity","data":{"activityType":"wmiQuery","label":"w","outputVariable":"w",
            "config":{"captureProperties":["Name","count","FreeSpace"]}}},
          {"id":"end","type":"activity","data":{"activityType":"log","label":"end","config":{}}}],
         "edges":[
          {"id":"e0","source":"t","target":"w"},
          {"id":"e1","source":"w","target":"end"}]}
        """);

        var vars = VariableResolver.Available(def, "end");

        vars.Upstream.Should().Contain("{{w.param.count}}");
        vars.Upstream.Should().Contain("{{w.param.Name}}");
        vars.Upstream.Should().Contain("{{w.param.FreeSpace}}");
        // "count" appears once despite being both auto-yielded and listed as a capture prop.
        vars.Upstream.Count(v => v == "{{w.param.count}}").Should().Be(1);
    }

    [Fact]
    public void Available_RegistryOperations_ExposeOperationSpecificParams()
    {
        // One registryOperation per operation arm chained from the trigger, so Available("end")
        // collects them all. Exercises every branch of RegistryParams, incl. the default (delete)
        // and the read-without-valueName path (which drives TryGetString's false return).
        var def = E("""
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"t","config":{}}},
          {"id":"rlv","type":"activity","data":{"activityType":"registryOperation","label":"rlv","outputVariable":"rlv","config":{"operation":"listValues"}}},
          {"id":"rss","type":"activity","data":{"activityType":"registryOperation","label":"rss","outputVariable":"rss","config":{"operation":"listSubKeys"}}},
          {"id":"rex","type":"activity","data":{"activityType":"registryOperation","label":"rex","outputVariable":"rex","config":{"operation":"exists"}}},
          {"id":"rck","type":"activity","data":{"activityType":"registryOperation","label":"rck","outputVariable":"rck","config":{"operation":"createKey"}}},
          {"id":"rw","type":"activity","data":{"activityType":"registryOperation","label":"rw","outputVariable":"rw","config":{"operation":"write"}}},
          {"id":"rdef","type":"activity","data":{"activityType":"registryOperation","label":"rdef","outputVariable":"rdef","config":{"operation":"delete"}}},
          {"id":"rempty","type":"activity","data":{"activityType":"registryOperation","label":"rempty","outputVariable":"rempty","config":{}}},
          {"id":"end","type":"activity","data":{"activityType":"log","label":"end","config":{}}}],
         "edges":[
          {"id":"e0","source":"t","target":"rlv"},
          {"id":"e1","source":"rlv","target":"rss"},
          {"id":"e2","source":"rss","target":"rex"},
          {"id":"e3","source":"rex","target":"rck"},
          {"id":"e4","source":"rck","target":"rw"},
          {"id":"e5","source":"rw","target":"rdef"},
          {"id":"e6","source":"rdef","target":"rempty"},
          {"id":"e7","source":"rempty","target":"end"}]}
        """);

        var vars = VariableResolver.Available(def, "end");

        vars.Upstream.Should().Contain("{{rlv.param.values}}").And.Contain("{{rlv.param.count}}");
        vars.Upstream.Should().Contain("{{rss.param.subKeys}}").And.Contain("{{rss.param.count}}");
        vars.Upstream.Should().Contain("{{rex.param.exists}}");
        vars.Upstream.Should().Contain("{{rck.param.created}}");
        vars.Upstream.Should().Contain("{{rw.param.type}}");
        // default arm (unknown operation) yields no dynamic params — only the base output triad.
        vars.Upstream.Should().Contain("{{rdef.output}}");
        vars.Upstream.Should().NotContain("{{rdef.param.values}}");
        // read without a valueName → values/count (ternary false branch).
        vars.Upstream.Should().Contain("{{rempty.param.values}}").And.Contain("{{rempty.param.count}}");
    }

    [Fact]
    public void Available_ManualTriggerParameters_SurfaceAsRunLevel()
    {
        var def = E("""
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"t",
            "config":{"parameters":[{"name":"env"},{"name":"region"}]}}},
          {"id":"end","type":"activity","data":{"activityType":"log","label":"end","config":{}}}],
         "edges":[{"id":"e","source":"t","target":"end"}]}
        """);

        var vars = VariableResolver.Available(def, "end");

        vars.RunLevel.Should().Contain("{{manual.env}}").And.Contain("{{manual.region}}");
    }
}
