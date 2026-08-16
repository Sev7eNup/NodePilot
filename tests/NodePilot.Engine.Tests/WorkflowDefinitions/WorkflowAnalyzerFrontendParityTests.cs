using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Engine.Tests.WorkflowDefinitions;

/// <summary>
/// The single mirror guard for the static workflow analysis. <c>WorkflowAnalyzer</c> and
/// <c>WorkflowDataBusAnalyzer</c> live in Core and serve BOTH the MCP tools and the AI chat, so
/// this file is the one place that pins their codes against the canvas linter
/// (<c>workflowLint.ts</c>). It used to sit in Mcp.Tests, back when the chat had its own second
/// analyzer that nothing pinned against anything.
/// </summary>
public sealed class WorkflowAnalyzerFrontendParityTests
{
    private static readonly string[] MirroredFrontendLintCodes =
    [
        "duplicate-edge",
        "dup-output-variable",
        "unknown-template-ref",
        "startjob-in-runspace",
    ];

    private static JsonElement E(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void FrontendLintCodes_MirroredByAnalyzer_StayPresentInFrontendSource()
    {
        var source = File.ReadAllText(PathFor(FindRepoRoot(), "src/nodepilot-ui/src/lib/workflowLint.ts"));

        foreach (var code in MirroredFrontendLintCodes)
            source.Should().Contain($"code: '{code}'", $"the shared analyzer mirrors the frontend lint rule '{code}'");
    }

    /// <summary>
    /// analyze_workflow must report unresolvable template references itself.
    ///
    /// <para>The check existed only behind find_unresolved_references, so the tool an agent
    /// actually calls answered ok:true / findings:[] for a workflow that fails on its first run.
    /// Measured against a 1.2.6 install: a log message reading {{gibtsnicht.output}} produced a
    /// clean analysis, and only the orphan-node finding came back for the same definition.</para>
    /// </summary>
    [Fact]
    public void AnalyzeWorkflow_FlagsUnresolvableTemplateReference_AsError()
    {
        var result = WorkflowAnalyzer.Analyze(E("""
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"Start","config":{}}},
          {"id":"a","type":"activity","data":{"activityType":"log","label":"Log","config":{"message":"x {{gibtsnicht.output}}"}}}],
         "edges":[{"id":"e1","source":"t","target":"a"}]}
        """));

        result.Findings.Should().Contain(f =>
            f.Code == "unknown-template-ref" && f.Severity == "error" && f.NodeId == "a");
        result.Ok.Should().BeFalse("a reference that aborts the step is an error, not a hint");
    }

    /// <summary>
    /// runScript resolves its own templates and tolerates a leftover {{...}} because it may be
    /// legitimate script text, so the same reference must not be reported as fatal there —
    /// reporting it as an error would make every script carrying brace syntax un-analysable.
    /// </summary>
    [Fact]
    public void AnalyzeWorkflow_UnresolvableReferenceInRunScript_IsWarningNotError()
    {
        var result = WorkflowAnalyzer.Analyze(E("""
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"Start","config":{}}},
          {"id":"s","type":"activity","data":{"activityType":"runScript","label":"Script","config":{"script":"Write-Output '{{gibtsnicht.output}}'"}}}],
         "edges":[{"id":"e1","source":"t","target":"s"}]}
        """));

        result.Findings.Should().Contain(f =>
            f.Code == "unknown-template-ref" && f.Severity == "warning" && f.NodeId == "s");
        result.Ok.Should().BeTrue("the run survives, so the analysis must not call it broken");
    }

    /// <summary>A reference on a disabled node cannot fail anything — that node never runs.</summary>
    [Fact]
    public void AnalyzeWorkflow_UnresolvableReferenceOnDisabledNode_IsNotReported()
    {
        var result = WorkflowAnalyzer.Analyze(E("""
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"Start","config":{}}},
          {"id":"a","type":"activity","data":{"activityType":"log","label":"Log","disabled":true,"config":{"message":"x {{gibtsnicht.output}}"}}}],
         "edges":[{"id":"e1","source":"t","target":"a"}]}
        """));

        result.Findings.Should().NotContain(f => f.Code == "unknown-template-ref");
    }

    [Fact]
    public void AnalyzeWorkflow_FlagsDuplicateEdges_WithFrontendCode()
    {
        var result = WorkflowAnalyzer.Analyze(E("""
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"Start","config":{}}},
          {"id":"a","type":"activity","data":{"activityType":"log","label":"Log","config":{}}}],
         "edges":[
          {"id":"e1","source":"t","target":"a"},
          {"id":"e2","source":"t","target":"a"}]}
        """));

        result.Ok.Should().BeFalse();
        result.Findings.Should().Contain(f =>
            f.Code == "duplicate-edge" && f.Severity == "error" && f.NodeId == "t");
    }

    [Fact]
    public void AnalyzeWorkflow_FlagsDuplicateOutputVariables_WithFrontendCode()
    {
        var result = WorkflowAnalyzer.Analyze(E("""
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"Start","config":{}}},
          {"id":"a","type":"activity","data":{"activityType":"log","label":"First","outputVariable":"shared","config":{}}},
          {"id":"b","type":"activity","data":{"activityType":"log","label":"Second","outputVariable":"shared","config":{}}}],
         "edges":[
          {"id":"e1","source":"t","target":"a"},
          {"id":"e2","source":"t","target":"b"}]}
        """));

        result.Ok.Should().BeFalse();
        result.Findings.Should().Contain(f =>
            f.Code == "dup-output-variable" && f.Severity == "error" && f.NodeId == "b");
    }

    [Fact]
    public void AnalyzeWorkflow_WarnsForStartJobInHostedRunspace_WithFrontendCode()
    {
        var result = WorkflowAnalyzer.Analyze(E("""
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"Start","config":{}}},
          {"id":"script","type":"activity","data":{"activityType":"runScript","label":"Script",
            "config":{"engine":"auto","script":"Start-Job { Get-Process }"}}}],
         "edges":[{"id":"e1","source":"t","target":"script"}]}
        """));

        result.Ok.Should().BeTrue();
        result.Findings.Should().Contain(f =>
            f.Code == "startjob-in-runspace" && f.Severity == "warning" && f.NodeId == "script");
    }

    [Fact]
    public void FindUnresolvedReferences_FlagsUnknownTemplateRef_WithFrontendCode()
    {
        var unresolved = WorkflowDataBusAnalyzer.FindUnresolved(E("""
        {"nodes":[
          {"id":"t","type":"activity","data":{"activityType":"manualTrigger","label":"Start","config":{}}},
          {"id":"use","type":"activity","data":{"activityType":"log","label":"Use","config":{"message":"{{ghost.output}}"}}}],
         "edges":[{"id":"e1","source":"t","target":"use"}]}
        """));

        unresolved.Should().Contain(r =>
            r.Code == "unknown-template-ref" && r.NodeId == "use" && r.Reference == "{{ghost.output}}");
    }

    [Fact]
    public void AvailableVariables_IncludeWebhookFieldMappings()
    {
        var vars = WorkflowDataBusAnalyzer.Available(E("""
        {"nodes":[
          {"id":"hook","type":"activity","data":{"activityType":"webhookTrigger","label":"Hook","outputVariable":"wh",
            "config":{"path":"incident","fieldMappings":[
              {"name":"ticketId","path":"$.ticket.id"},
              {"name":"severity","path":"$.ticket.severity"},
              {"name":"","path":"$.ignored"}]}}},
          {"id":"use","type":"activity","data":{"activityType":"log","label":"Use","config":{}}}],
         "edges":[{"id":"e1","source":"hook","target":"use"}]}
        """), "use");

        vars.Upstream.Should().Contain("{{wh.param.ticketId}}");
        vars.Upstream.Should().Contain("{{wh.param.severity}}");
        vars.Upstream.Should().Contain("{{wh.param.webhookBody}}", "static catalog outputs stay alongside dynamic mappings");
        vars.Upstream.Should().NotContain("{{wh.param.}}");
    }

    [Fact]
    public void AvailableVariables_IncludeFileWatcherStaticOutputs()
    {
        var vars = WorkflowDataBusAnalyzer.Available(E("""
        {"nodes":[
          {"id":"fw","type":"activity","data":{"activityType":"fileWatcherTrigger","label":"Watch","outputVariable":"watch",
            "config":{"directory":"C:\\inbox","filter":"*.csv","watchType":"created"}}},
          {"id":"use","type":"activity","data":{"activityType":"log","label":"Use","config":{}}}],
         "edges":[{"id":"e1","source":"fw","target":"use"}]}
        """), "use");

        vars.Upstream.Should().Contain("{{watch.param.fileAction}}");
        vars.Upstream.Should().Contain("{{watch.param.filePath}}");
        vars.Upstream.Should().Contain("{{watch.param.fileName}}");
    }

    [Fact]
    public void AvailableVariables_IncludeExternalTriggerStaticOutputs()
    {
        var vars = WorkflowDataBusAnalyzer.Available(E("""
        {"nodes":[
          {"id":"sched","type":"activity","data":{"activityType":"scheduleTrigger","label":"Schedule","outputVariable":"sched",
            "config":{"cronExpression":"0 0/5 * * * ?"}}},
          {"id":"db","type":"activity","data":{"activityType":"databaseTrigger","label":"DB","outputVariable":"db",
            "config":{"connectionRef":"prod","query":"select max(id) from Jobs"}}},
          {"id":"ev","type":"activity","data":{"activityType":"eventLogTrigger","label":"Event","outputVariable":"ev",
            "config":{"logName":"Application","entryType":"Error"}}},
          {"id":"use","type":"activity","data":{"activityType":"log","label":"Use","config":{}}}],
         "edges":[
          {"id":"e1","source":"sched","target":"use"},
          {"id":"e2","source":"db","target":"use"},
          {"id":"e3","source":"ev","target":"use"}]}
        """), "use");

        vars.Upstream.Should().Contain("{{sched.param.firedAt}}");
        vars.Upstream.Should().Contain("{{sched.param.nextFireAt}}");
        vars.Upstream.Should().Contain("{{db.param.dbSentinel}}");
        vars.Upstream.Should().Contain("{{db.param.dbPrevious}}");
        vars.Upstream.Should().Contain("{{ev.param.eventSource}}");
        vars.Upstream.Should().Contain("{{ev.param.eventEntryType}}");
        vars.Upstream.Should().Contain("{{ev.param.eventId}}");
        vars.Upstream.Should().Contain("{{ev.param.eventMessage}}");
        vars.Upstream.Should().Contain("{{ev.param.eventTimeWritten}}");
    }

    [Fact]
    public void DynamicDatabusActivityTypes_MatchFrontendUpstreamVariableProviders()
    {
        var repoRoot = FindRepoRoot();
        var frontend = StripComments(File.ReadAllText(PathFor(repoRoot, "src/nodepilot-ui/src/lib/upstreamVariables.ts")));
        var resolver = StripComments(File.ReadAllText(PathFor(repoRoot, "src/NodePilot.Core/WorkflowDefinitions/WorkflowDataBusAnalyzer.cs")));

        var frontendTypes = Regex.Matches(frontend, @"activityType\s*===\s*'(?<type>[^']+)'")
            .Select(m => m.Groups["type"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var dynamicParamsMatch = Regex.Match(
            resolver,
            @"DynamicParams\(WorkflowNode\s+node\)\s*=>\s*node\.Type\s+switch\s*\{(?<body>[\s\S]*?)\};",
            RegexOptions.Singleline);
        dynamicParamsMatch.Success.Should().BeTrue("WorkflowDataBusAnalyzer.DynamicParams must stay parseable by this drift guard");

        var resolverTypes = Regex.Matches(dynamicParamsMatch.Groups["body"].Value, @"""(?<type>[^""]+)""\s*=>")
            .Select(m => m.Groups["type"].Value)
            .ToHashSet(StringComparer.Ordinal);

        resolverTypes.Should().BeEquivalentTo(
            frontendTypes,
            "the frontend variable picker and get_available_variables must expose dynamic databus params for the same activity types");
    }

    [Fact]
    public void RuntimeTemplateNamespaces_MatchAcrossFrontendLintAndAnalyzer()
    {
        var repoRoot = FindRepoRoot();
        var workflowLint = StripComments(File.ReadAllText(PathFor(repoRoot, "src/nodepilot-ui/src/lib/workflowLint.ts")));
        var variableUsageScan = StripComments(File.ReadAllText(PathFor(repoRoot, "src/nodepilot-ui/src/lib/variableUsageScan.ts")));
        var resolver = StripComments(File.ReadAllText(PathFor(repoRoot, "src/NodePilot.Core/WorkflowDefinitions/WorkflowDataBusAnalyzer.cs")));

        var workflowLintPrefixes = ReadTypeScriptStringSet(workflowLint, "runtimePrefixes");
        var variableUsagePrefixes = ReadTypeScriptStringSet(variableUsageScan, "RUNTIME_HEADS");
        var resolverPrefixes = Regex.Matches(resolver, @"head\.Equals\(""(?<name>[^""]+)""")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        workflowLintPrefixes.Should().BeEquivalentTo(["globals", "manual"]);
        variableUsagePrefixes.Should().BeEquivalentTo(workflowLintPrefixes);
        resolverPrefixes.Should().BeEquivalentTo(
            workflowLintPrefixes,
            "UI lint, data-flow scanning, and the analyzer's unresolved-reference checks must agree on the runtime-injected namespaces");
    }

    private static string StripComments(string content)
        => Regex.Replace(content, @"/\*[\s\S]*?\*/|//.*", "", RegexOptions.Multiline);

    private static HashSet<string> ReadTypeScriptStringSet(string source, string variableName)
    {
        var match = Regex.Match(
            source,
            $@"const\s+{Regex.Escape(variableName)}\s*=\s*new\s+Set\s*\(\s*\[(?<body>[^\]]*)\]\s*\)",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue($"{variableName} must stay parseable by this drift guard");

        return Regex.Matches(match.Groups["body"].Value, @"'(?<value>[^']+)'")
            .Select(m => m.Groups["value"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string PathFor(string repoRoot, string relativePath)
        => Path.Combine([repoRoot, .. relativePath.Split('/')]);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }
}
