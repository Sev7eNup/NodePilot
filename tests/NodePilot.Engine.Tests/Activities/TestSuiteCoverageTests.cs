using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// Drift guard for the generated test suite under <c>scripts/test-suite/</c>.
///
/// <para>The suite exists to prove every activity variation still works, and it decayed
/// twice before because nothing checked it. Asserting that a value merely appears
/// somewhere in a config is not enough: it could sit on a disabled node, on a node no
/// trigger can reach, or on one whose result nothing ever looks at. So every manifest case
/// is checked for reachability from an active trigger and for having something that
/// actually judges it.</para>
/// </summary>
public class TestSuiteCoverageTests
{
    private static readonly string[] AllowedCrons =
    [
        "0 0/5 * * * ? *", "0 1/5 * * * ? *", "0 2/5 * * * ? *", "0 3/5 * * * ? *",
        "0 4/5 * * * ? *", "0 5/15 * * * ? *", "0 10/15 * * * ? *",
        "0 7/30 * * * ? *", "0 22/30 * * * ? *", "0 3/10 * * * ? *"
    ];

    private static readonly Dictionary<string, int> TierSeconds = new()
    {
        ["A"] = 300, ["B"] = 900, ["C"] = 1800, ["D"] = 600
    };

    [Fact]
    public void EveryCatalogActivity_HasSuiteCoverage()
    {
        var suite = LoadSuite();
        var catalog = LoadCatalogActivityTypes();

        var covered = suite.Cases
            .Select(c => c.Dimension.Split('.')[0])
            .ToHashSet(StringComparer.Ordinal);

        var missing = catalog.Where(t => !covered.Contains(t)).OrderBy(t => t).ToList();
        missing.Should().BeEmpty(
            "every type in activity-config-reference.json needs at least one case in " +
            "scripts/test-suite/suite-manifest.json; add one or declare it excluded with a reason");
    }

    [Fact]
    public void EveryCase_PointsAtANodeThatIsReachableAndJudged()
    {
        var suite = LoadSuite();
        var problems = new List<string>();

        foreach (var c in suite.Cases.Where(c => c.ExpectedOutcome != "excluded"))
        {
            if (!suite.Definitions.TryGetValue(c.Workflow, out var def))
            {
                problems.Add($"{c.Id}: workflow '{c.Workflow}' has no definition file");
                continue;
            }

            var node = def.Nodes.FirstOrDefault(n => n.Id == c.NodeId);
            if (node is null)
            {
                problems.Add($"{c.Id}: node '{c.NodeId}' does not exist in '{c.Workflow}'");
                continue;
            }
            // A case that asserts a node must be Skipped is testing exactly that it is
            // unreachable - a disabled node, or one behind a disabled or unmet edge.
            var expectsSkipped = c.ExpectedStepStatus == "Skipped";
            if (!expectsSkipped)
            {
                if (node.Data.Disabled)
                    problems.Add($"{c.Id}: node '{c.NodeId}' is disabled, so the value is never exercised");
                if (!def.ReachableFromActiveTrigger.Contains(c.NodeId))
                    problems.Add($"{c.Id}: node '{c.NodeId}' is not reachable from an active trigger");
            }

            // Something has to look at the outcome. Either an assert node downstream reads
            // this node's output variable, or the verifier judges it from the manifest.
            if (c.ExpectedOutcome == "success" && c.AssertedBy is { Length: > 0 } asserter
                && !asserter.StartsWith("verifier:", StringComparison.Ordinal))
            {
                var assertNode = def.Nodes.FirstOrDefault(n => n.Id == asserter);
                if (assertNode is null)
                {
                    problems.Add($"{c.Id}: assertedBy '{asserter}' is not a node in '{c.Workflow}'");
                }
                else
                {
                    // A variant is often proven through another node: a create by the
                    // exists that follows it, an encoding by the bytes a later script
                    // reads back. assertedVia names that node; without it the assertion
                    // has to reference the variant node itself.
                    var witnessId = c.AssertedVia ?? c.NodeId;
                    var witness = def.Nodes.FirstOrDefault(n => n.Id == witnessId);
                    if (witness is null)
                    {
                        problems.Add($"{c.Id}: assertedVia '{witnessId}' is not a node in '{c.Workflow}'");
                    }
                    else if (!assertNode.Data.ConfigText.Contains(witness.Data.OutputVariable, StringComparison.Ordinal))
                    {
                        problems.Add(
                            $"{c.Id}: the assert node does not reference '{witness.Data.OutputVariable}', " +
                            "so the variant runs but nothing checks its result");
                    }
                    else if (c.AssertedVia is not null && !def.ReachableFromActiveTrigger.Contains(witnessId))
                    {
                        problems.Add($"{c.Id}: the witness node '{witnessId}' is itself unreachable");
                    }
                }
            }
        }

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void ExcludedCases_CarryAReasonAndAValidPointer()
    {
        var suite = LoadSuite();
        var problems = new List<string>();

        foreach (var c in suite.Cases.Where(c => c.ExpectedOutcome == "excluded"))
        {
            if (string.IsNullOrWhiteSpace(c.Reason))
                problems.Add($"{c.Id}: an excluded case must say why");
            if (!string.IsNullOrWhiteSpace(c.CoveredBy))
            {
                var path = Path.Combine(FindRepoRoot(), c.CoveredBy.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path) && !Directory.Exists(path))
                    problems.Add($"{c.Id}: coveredBy '{c.CoveredBy}' does not exist");
            }
        }

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryWorkflow_HasOneTriggerAndAContainableRuntime()
    {
        var suite = LoadSuite();
        var problems = new List<string>();

        foreach (var wf in suite.Workflows)
        {
            var def = suite.Definitions[wf.Name];

            var triggers = def.Nodes.Where(n => n.Data.ActivityType.EndsWith("Trigger", StringComparison.Ordinal))
                .ToList();
            if (triggers.Count != 1)
                problems.Add($"{wf.Name}: expected exactly one trigger node, found {triggers.Count}");

            if (wf.Tier is not null)
            {
                var cron = triggers.FirstOrDefault()?.Data.CronExpression;
                if (cron != wf.Cron)
                    problems.Add($"{wf.Name}: manifest cron '{wf.Cron}' but the definition says '{cron}'");
                if (cron is null || !AllowedCrons.Contains(cron))
                    problems.Add($"{wf.Name}: cron '{cron}' is outside the declared tiers");

                // Beyond half its cadence a run stacks up behind MaxConcurrentExecutions=1
                // and is reported as deferred rather than failing loudly.
                var budget = TierSeconds[wf.Tier] / 2;
                if (wf.MaxRuntimeSeconds > budget)
                    problems.Add($"{wf.Name}: maxRuntime {wf.MaxRuntimeSeconds}s exceeds half of the tier-{wf.Tier} cadence ({budget}s)");
            }

            if (!wf.Name.StartsWith("[TestSuite", StringComparison.Ordinal))
                problems.Add($"{wf.Name}: suite workflows are named with a [TestSuite...] prefix");

            var ids = def.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var e in def.Edges)
            {
                if (!ids.Contains(e.Source) || !ids.Contains(e.Target))
                    problems.Add($"{wf.Name}: edge {e.Source} -> {e.Target} points at a node that does not exist");
            }

            // Only a junction may take more than one incoming edge; anything else is
            // rejected by the structure validation on save.
            foreach (var group in def.Edges.GroupBy(e => e.Target).Where(g => g.Count() > 1))
            {
                var target = def.Nodes.First(n => n.Id == group.Key);
                if (target.Data.ActivityType != "junction")
                    problems.Add($"{wf.Name}: '{group.Key}' is a {target.Data.ActivityType} with {group.Count()} incoming edges");
            }

            foreach (var n in def.Nodes.Where(n => n.Position.X % 20 != 0 || n.Position.Y % 20 != 0))
                problems.Add($"{wf.Name}: node '{n.Id}' is off the 20 px grid at ({n.Position.X},{n.Position.Y})");
        }

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
    }

    // --- loading -------------------------------------------------------------------

    private static SuiteModel LoadSuite()
    {
        var root = Path.Combine(FindRepoRoot(), "scripts", "test-suite");
        var manifestPath = Path.Combine(root, "suite-manifest.json");
        File.Exists(manifestPath).Should().BeTrue(
            $"{manifestPath} must exist; regenerate it with python scripts/test-suite/build_suite.py");

        var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;

        var workflows = manifest.GetProperty("workflows").EnumerateArray()
            .Select(w => new SuiteWorkflow(
                w.GetProperty("name").GetString()!,
                w.GetProperty("file").GetString()!,
                w.GetProperty("tier").ValueKind == JsonValueKind.Null ? null : w.GetProperty("tier").GetString(),
                w.GetProperty("cron").ValueKind == JsonValueKind.Null ? null : w.GetProperty("cron").GetString(),
                w.GetProperty("maxRuntimeSeconds").GetInt32()))
            .ToList();

        var cases = manifest.GetProperty("cases").EnumerateArray()
            .Select(c => new SuiteCase(
                c.GetProperty("id").GetString()!,
                c.GetProperty("dimension").GetString()!,
                c.GetProperty("workflow").GetString()!,
                c.TryGetProperty("nodeId", out var n) ? n.GetString() : null,
                c.GetProperty("expectedOutcome").GetString()!,
                c.TryGetProperty("assertedBy", out var a) ? a.GetString() : null,
                c.TryGetProperty("assertedVia", out var av) ? av.GetString() : null,
                c.TryGetProperty("expectedStepStatus", out var ess)
                    ? ess.GetProperty("status").GetString() : null,
                c.TryGetProperty("reason", out var r) ? r.GetString() : null,
                c.TryGetProperty("coveredBy", out var cb) ? cb.GetString() : null))
            .ToList();

        var definitions = new Dictionary<string, WorkflowDefinition>(StringComparer.Ordinal);
        foreach (var wf in workflows)
        {
            var path = Path.Combine(root, wf.File.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue($"{path} is listed in the manifest");
            definitions[wf.Name] = WorkflowDefinition.Parse(File.ReadAllText(path));
        }

        return new SuiteModel(workflows, cases, definitions);
    }

    private static HashSet<string> LoadCatalogActivityTypes()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "NodePilot.Core", "Activities",
            "Embedded", "activity-config-reference.json");
        var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("activities").EnumerateObject()
            .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException(
            $"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }

    private sealed record SuiteModel(
        List<SuiteWorkflow> Workflows,
        List<SuiteCase> Cases,
        Dictionary<string, WorkflowDefinition> Definitions);

    private sealed record SuiteWorkflow(string Name, string File, string? Tier, string? Cron, int MaxRuntimeSeconds);

    private sealed record SuiteCase(
        string Id, string Dimension, string Workflow, string? NodeId, string ExpectedOutcome,
        string? AssertedBy, string? AssertedVia, string? ExpectedStepStatus,
        string? Reason, string? CoveredBy);

    private sealed record NodePosition(int X, int Y);

    private sealed record NodeData(
        string ActivityType, string OutputVariable, bool Disabled, string ConfigText, string? CronExpression);

    private sealed record SuiteNode(string Id, NodePosition Position, NodeData Data);

    private sealed record SuiteEdge(string Source, string Target, bool Disabled);

    private sealed class WorkflowDefinition
    {
        public required List<SuiteNode> Nodes { get; init; }
        public required List<SuiteEdge> Edges { get; init; }

        /// <summary>Node ids a run can actually arrive at: a walk from every enabled
        /// trigger, refusing to cross disabled edges or enter disabled nodes.</summary>
        public HashSet<string> ReachableFromActiveTrigger
        {
            get
            {
                var reachable = new HashSet<string>(StringComparer.Ordinal);
                var queue = new Queue<string>();
                foreach (var t in Nodes.Where(n =>
                             n.Data.ActivityType.EndsWith("Trigger", StringComparison.Ordinal)
                             && !n.Data.Disabled))
                {
                    reachable.Add(t.Id);
                    queue.Enqueue(t.Id);
                }

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    foreach (var e in Edges.Where(e => e.Source == current && !e.Disabled))
                    {
                        var target = Nodes.FirstOrDefault(n => n.Id == e.Target);
                        if (target is null || target.Data.Disabled) continue;
                        if (reachable.Add(e.Target)) queue.Enqueue(e.Target);
                    }
                }
                return reachable;
            }
        }

        public static WorkflowDefinition Parse(string envelopeJson)
        {
            var definition = JsonDocument.Parse(envelopeJson).RootElement
                .GetProperty("workflows")[0].GetProperty("definition");

            var nodes = definition.GetProperty("nodes").EnumerateArray().Select(n =>
            {
                var data = n.GetProperty("data");
                var config = data.TryGetProperty("config", out var c) ? c : default;
                return new SuiteNode(
                    n.GetProperty("id").GetString()!,
                    new NodePosition(
                        n.GetProperty("position").GetProperty("x").GetInt32(),
                        n.GetProperty("position").GetProperty("y").GetInt32()),
                    new NodeData(
                        data.GetProperty("activityType").GetString()!,
                        data.TryGetProperty("outputVariable", out var ov)
                            ? ov.GetString()! : n.GetProperty("id").GetString()!,
                        data.TryGetProperty("disabled", out var d) && d.GetBoolean(),
                        config.ValueKind == JsonValueKind.Undefined ? string.Empty : config.GetRawText(),
                        config.ValueKind != JsonValueKind.Undefined
                        && config.TryGetProperty("cronExpression", out var cron)
                            ? cron.GetString() : null));
            }).ToList();

            var edges = definition.GetProperty("edges").EnumerateArray().Select(e =>
            {
                var data = e.TryGetProperty("data", out var ed) ? ed : default;
                return new SuiteEdge(
                    e.GetProperty("source").GetString()!,
                    e.GetProperty("target").GetString()!,
                    data.ValueKind != JsonValueKind.Undefined
                    && data.TryGetProperty("disabled", out var dd) && dd.GetBoolean());
            }).ToList();

            return new WorkflowDefinition { Nodes = nodes, Edges = edges };
        }
    }
}
