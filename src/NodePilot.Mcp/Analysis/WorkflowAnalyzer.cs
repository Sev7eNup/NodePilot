using System.Text.Json;
using System.Text.RegularExpressions;
using NodePilot.Core.Activities;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Mcp.Analysis;

/// <summary>
/// In-process static analysis of a workflow definition (works on the unsaved canvas state).
/// Surfaces the NodePilot-specific traps an author hits most: nodes that never run (no active
/// path from a trigger — "trigger-only roots"), a missing/disabled trigger (0 roots → the run
/// Fails), cycles, remote activities without a target machine, and unknown activity types.
/// </summary>
public static class WorkflowAnalyzer
{
    public sealed record Finding(string Severity, string Code, string? NodeId, string Message);

    public sealed record AnalysisResult(
        bool Ok, int NodeCount, int EdgeCount, IReadOnlyList<string> Roots, IReadOnlyList<Finding> Findings);

    public static AnalysisResult Analyze(JsonElement definition)
    {
        var doc = WorkflowDefinitionDocument.FromJsonElement(definition);
        var findings = new List<Finding>();

        var rootIds = doc.RootNodes.Select(n => n.Id).ToList();
        if (rootIds.Count == 0)
        {
            findings.Add(new Finding("error", "no-trigger", null,
                "No active trigger node — execution would Fail (0 roots). Roots are exclusively enabled trigger nodes; add/enable a trigger."));
        }

        AddDuplicateEdgeFindings(doc, findings);

        // Reachability from the trigger roots over ACTIVE edges. Anything else never runs.
        var reachable = ReachableFrom(rootIds, doc.Adjacency);
        foreach (var node in doc.Nodes)
        {
            if (doc.DisabledNodeIds.Contains(node.Id)) continue;
            if (IsAnnotation(node.Type)) continue;
            if (ActivityCatalog.TriggerTypes.Contains(node.Type)) continue; // triggers are roots, not "downstream"
            if (reachable.Contains(node.Id)) continue;
            findings.Add(new Finding("warning", "unreachable-node", node.Id,
                $"Node '{Label(node)}' never runs (Skipped): no active path from a trigger (orphan / disconnected / all incoming edges disabled)."));
        }

        // Cycles in the active graph.
        if (FindCycle(doc.Adjacency) is { Count: > 0 } cycle)
            findings.Add(new Finding("error", "cycle", null, $"Cycle detected: {string.Join(" → ", cycle)} → {cycle[0]}. The engine has no inDegree fallback; cyclic graphs Fail."));

        AddDuplicateOutputVariableFindings(doc, findings);

        // Remote activities without a target machine, and unknown activity types.
        foreach (var node in doc.Nodes)
        {
            if (doc.DisabledNodeIds.Contains(node.Id)) continue;

            // custom:<key> activities are user-authored and resolved at run time, not in the static
            // catalog — recognise them by prefix so they aren't flagged as unknown. Their RunsRemote
            // flag isn't resolvable here (no DB), so the remote-target heuristic below skips them.
            if (!ActivityCatalog.ByType.ContainsKey(node.Type) && !IsAnnotation(node.Type)
                && !CustomActivityType.IsCustomType(node.Type))
            {
                findings.Add(new Finding("error", "unknown-activity-type", node.Id, $"Unknown activityType '{node.Type}'."));
                continue;
            }

            // runScript and waitForCondition are HYBRID: without a target machine they run locally
            // in the API process (Localhost-Bypass), so a missing targetMachineId is NOT an error.
            if (ActivityCatalog.RemoteTypes.Contains(node.Type)
                && !HybridLocalTypes.Contains(node.Type)
                && !CustomActivityType.IsCustomType(node.Type)
                && string.IsNullOrWhiteSpace(node.Data.TargetMachineRaw))
                findings.Add(new Finding("warning", "missing-target-machine", node.Id,
                    $"Remote activity '{node.Type}' on '{Label(node)}' has no targetMachineId — it cannot run against a host."));
        }

        AddStartJobRunspaceFindings(doc, findings);

        var ok = !findings.Any(f => f.Severity == "error");
        return new AnalysisResult(ok, doc.Nodes.Count, doc.Edges.Count, rootIds, findings);
    }

    private static void AddDuplicateEdgeFindings(WorkflowDefinitionDocument doc, List<Finding> findings)
    {
        var liveNodeIds = doc.Nodes
            .Where(n => !IsAnnotation(n.Type))
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);
        var seen = new Dictionary<(string Source, string Target), string>();

        foreach (var edge in doc.Edges)
        {
            if (!liveNodeIds.Contains(edge.Source) || !liveNodeIds.Contains(edge.Target))
                continue;

            var key = (edge.Source, edge.Target);
            if (seen.TryGetValue(key, out var firstEdgeId))
            {
                findings.Add(new Finding(
                    "error",
                    "duplicate-edge",
                    edge.Source,
                    $"Connection {edge.Source} -> {edge.Target} already exists ({firstEdgeId}); edit the existing edge instead of adding a second one."));
            }
            else
            {
                seen[key] = edge.Id;
            }
        }
    }

    private static void AddDuplicateOutputVariableFindings(WorkflowDefinitionDocument doc, List<Finding> findings)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in doc.Nodes)
        {
            if (doc.DisabledNodeIds.Contains(node.Id)) continue;
            if (IsAnnotation(node.Type)) continue;
            var outputVariable = node.Data.OutputVariable;
            if (string.IsNullOrWhiteSpace(outputVariable)) continue;

            if (seen.TryGetValue(outputVariable, out var firstNodeId))
            {
                findings.Add(new Finding(
                    "error",
                    "dup-output-variable",
                    node.Id,
                    $"outputVariable '{outputVariable}' is already used by node '{firstNodeId}' — downstream references {{{{{outputVariable}.output}}}} are ambiguous."));
            }
            else
            {
                seen[outputVariable] = node.Id;
            }
        }
    }

    private static void AddStartJobRunspaceFindings(WorkflowDefinitionDocument doc, List<Finding> findings)
    {
        foreach (var node in doc.Nodes)
        {
            if (doc.DisabledNodeIds.Contains(node.Id)) continue;
            if (!string.Equals(node.Type, "runScript", StringComparison.Ordinal)) continue;

            var config = node.Data.Config;
            var engine = TryGetString(config, "engine", out var rawEngine) && !string.IsNullOrWhiteSpace(rawEngine)
                ? rawEngine!.ToLowerInvariant()
                : "auto";
            if (engine is not ("auto" or "runspace")) continue;
            if (!TryGetString(config, "script", out var script) || string.IsNullOrWhiteSpace(script)) continue;

            var hit = StartJobHostedIncompatible.FirstOrDefault(p => p.Pattern.IsMatch(script!));
            if (hit is null) continue;

            findings.Add(new Finding(
                "warning",
                "startjob-in-runspace",
                node.Id,
                $"Script calls {hit.CmdletName}, which starts a background job. The hosted runspace engine ('{engine}') cannot run job-spawning cmdlets; set config.engine to 'pwsh' or remove the job-spawning command."));
        }
    }

    private static HashSet<string> ReachableFrom(IEnumerable<string> roots, IReadOnlyDictionary<string, List<string>> adjacency)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var r in roots) if (seen.Add(r)) queue.Enqueue(r);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!adjacency.TryGetValue(cur, out var next)) continue;
            foreach (var t in next) if (seen.Add(t)) queue.Enqueue(t);
        }
        return seen;
    }

    // Returns the nodes of one detected cycle, or null if the active graph is acyclic.
    private static List<string>? FindCycle(IReadOnlyDictionary<string, List<string>> adjacency)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0=unseen,1=on-stack,2=done
        var stack = new List<string>();

        foreach (var start in adjacency.Keys)
        {
            if (state.GetValueOrDefault(start) != 0) continue;
            var cycle = Dfs(start, adjacency, state, stack);
            if (cycle is not null) return cycle;
        }
        return null;
    }

    private static List<string>? Dfs(string node, IReadOnlyDictionary<string, List<string>> adjacency, Dictionary<string, int> state, List<string> stack)
    {
        state[node] = 1;
        stack.Add(node);
        if (adjacency.TryGetValue(node, out var next))
        {
            foreach (var n in next)
            {
                var s = state.GetValueOrDefault(n);
                if (s == 1)
                {
                    var idx = stack.IndexOf(n);
                    return stack.GetRange(idx, stack.Count - idx);
                }
                if (s == 0)
                {
                    var cycle = Dfs(n, adjacency, state, stack);
                    if (cycle is not null) return cycle;
                }
            }
        }
        stack.RemoveAt(stack.Count - 1);
        state[node] = 2;
        return null;
    }

    // Mirrors the UI lint's HYBRID_LOCAL_ACTIVITY_TYPES: remote-capable but valid locally.
    private static readonly HashSet<string> HybridLocalTypes =
        new(StringComparer.Ordinal) { "runScript", "waitForCondition" };

    private sealed record HostedIncompatiblePattern(Regex Pattern, string CmdletName);

    private static readonly HostedIncompatiblePattern[] StartJobHostedIncompatible =
    [
        new(new Regex(@"(^|[\s|;&])Start-Job\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Start-Job"),
        new(new Regex(@"\bGet-WindowsUpdateLog\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Get-WindowsUpdateLog (uses Start-Job internally)"),
        new(new Regex(@"\bInvoke-Command\b[^\r\n]*-AsJob\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Invoke-Command -AsJob"),
    ];

    private static bool IsAnnotation(string type)
        => string.Equals(type, "note", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "stickyNote", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "group", StringComparison.OrdinalIgnoreCase);

    private static string Label(NodePilot.Core.Models.WorkflowNode node)
        => string.IsNullOrWhiteSpace(node.Data.Label) ? node.Id : node.Data.Label!;

    private static bool TryGetString(JsonElement obj, string name, out string? value)
    {
        value = null;
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return value is not null;
        }

        return false;
    }
}
