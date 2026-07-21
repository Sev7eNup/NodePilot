using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Mcp.Analysis;
using Xunit;

namespace NodePilot.Mcp.Tests;

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
    public void FrontendLintCodes_MirroredByMcp_StayPresentInFrontendSource()
    {
        var source = File.ReadAllText(PathFor(FindRepoRoot(), "src/nodepilot-ui/src/lib/workflowLint.ts"));

        foreach (var code in MirroredFrontendLintCodes)
            source.Should().Contain($"code: '{code}'", $"MCP mirrors the frontend lint rule '{code}'");
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
        var unresolved = VariableResolver.FindUnresolved(E("""
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
        var vars = VariableResolver.Available(E("""
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
        var vars = VariableResolver.Available(E("""
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
        var vars = VariableResolver.Available(E("""
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
        var mcp = StripComments(File.ReadAllText(PathFor(repoRoot, "src/NodePilot.Mcp/Analysis/VariableResolver.cs")));

        var frontendTypes = Regex.Matches(frontend, @"activityType\s*===\s*'(?<type>[^']+)'")
            .Select(m => m.Groups["type"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var dynamicParamsMatch = Regex.Match(
            mcp,
            @"DynamicParams\(WorkflowNode\s+node\)\s*=>\s*node\.Type\s+switch\s*\{(?<body>[\s\S]*?)\};",
            RegexOptions.Singleline);
        dynamicParamsMatch.Success.Should().BeTrue("MCP VariableResolver.DynamicParams must stay parseable by this drift guard");

        var mcpTypes = Regex.Matches(dynamicParamsMatch.Groups["body"].Value, @"""(?<type>[^""]+)""\s*=>")
            .Select(m => m.Groups["type"].Value)
            .ToHashSet(StringComparer.Ordinal);

        mcpTypes.Should().BeEquivalentTo(
            frontendTypes,
            "the frontend variable picker and MCP get_available_variables must expose dynamic databus params for the same activity types");
    }

    [Fact]
    public void RuntimeTemplateNamespaces_MatchAcrossFrontendLintAndMcpResolver()
    {
        var repoRoot = FindRepoRoot();
        var workflowLint = StripComments(File.ReadAllText(PathFor(repoRoot, "src/nodepilot-ui/src/lib/workflowLint.ts")));
        var variableUsageScan = StripComments(File.ReadAllText(PathFor(repoRoot, "src/nodepilot-ui/src/lib/variableUsageScan.ts")));
        var mcpResolver = StripComments(File.ReadAllText(PathFor(repoRoot, "src/NodePilot.Mcp/Analysis/VariableResolver.cs")));

        var workflowLintPrefixes = ReadTypeScriptStringSet(workflowLint, "runtimePrefixes");
        var variableUsagePrefixes = ReadTypeScriptStringSet(variableUsageScan, "RUNTIME_HEADS");
        var mcpPrefixes = Regex.Matches(mcpResolver, @"head\.Equals\(""(?<name>[^""]+)""")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        workflowLintPrefixes.Should().BeEquivalentTo(["globals", "manual"]);
        variableUsagePrefixes.Should().BeEquivalentTo(workflowLintPrefixes);
        mcpPrefixes.Should().BeEquivalentTo(
            workflowLintPrefixes,
            "UI lint, data-flow scanning, and MCP unresolved-reference checks must agree on the runtime-injected namespaces");
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
