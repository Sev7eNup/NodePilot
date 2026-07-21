using System.Text.Json;
using NodePilot.Core.Activities;
using NodePilot.Core.Models;

namespace NodePilot.Core.WorkflowDefinitions;

public enum ReviewSeverity { Error, Warning, Info }

/// <summary>A single finding from the static workflow analysis.</summary>
public sealed record ReviewFinding(ReviewSeverity Severity, string Code, string Message, string? NodeId = null);

/// <summary>
/// Deterministic, read-only static analysis of a workflow definition — the basis of the
/// <c>analyze_workflow</c> chat tool (AI assistant). Deliberately reuses existing semantics instead
/// of reinterpreting them: <see cref="WorkflowDefinitionDocument"/> supplies the exact
/// root/reachability/disabled-edge semantics the engine itself uses, and
/// <see cref="WorkflowDefinitionStructuralValidator"/> supplies the structural checks (duplicate
/// IDs, dangling references). Finding codes/severities are kept aligned with the canvas linter
/// (<c>workflowLint.ts</c>) so the chat review and the designer linter never disagree.
/// </summary>
public static class WorkflowReviewAnalyzer
{
    public static IReadOnlyList<ReviewFinding> Analyze(JsonElement definition)
    {
        var findings = new List<ReviewFinding>();

        var structural = WorkflowDefinitionStructuralValidator.Validate(definition);
        if (!structural.IsValid)
        {
            // If the structure is broken (duplicate IDs, dangling references, ...) the graph analysis
            // below can't be trusted, so bail out early instead of reporting misleading findings.
            findings.Add(new ReviewFinding(ReviewSeverity.Error, "invalid-structure",
                structural.Error ?? "Definition ist strukturell ungültig."));
            return findings;
        }

        WorkflowDefinitionDocument doc;
        try { doc = WorkflowDefinitionDocument.FromJsonElement(definition); }
        catch (Exception) { return findings; }

        if (doc.Nodes.Count == 0) return findings; // empty workflow → nothing to report

        if (doc.RootNodes.Count == 0)
        {
            findings.Add(new ReviewFinding(ReviewSeverity.Error, "no-trigger",
                "Der Workflow hat keinen aktiven Trigger — er würde nie starten."));
        }

        var reachable = ReachableFromRoots(doc);
        foreach (var n in doc.Nodes)
        {
            if (n.Data.Disabled) continue;                              // deliberately turned off → no finding
            if (!ActivityCatalog.ByType.ContainsKey(n.Type)) continue;  // annotation nodes (stickyNote/group)
            if (ActivityCatalog.TriggerTypes.Contains(n.Type)) continue; // triggers are roots, not orphans

            if (!reachable.Contains(n.Id))
            {
                findings.Add(new ReviewFinding(ReviewSeverity.Warning, "orphan-node",
                    $"\"{Label(n)}\" ist von keinem Trigger erreichbar — der Step läuft nie.", n.Id));
            }
            if (ActivityCatalog.RemoteTypes.Contains(n.Type) && string.IsNullOrWhiteSpace(n.Data.TargetMachineRaw))
            {
                findings.Add(new ReviewFinding(ReviewSeverity.Warning, "missing-target-machine",
                    $"\"{Label(n)}\" ist eine Remote-Activity ohne Ziel-Maschine.", n.Id));
            }
        }

        if (HasCycle(doc))
        {
            findings.Add(new ReviewFinding(ReviewSeverity.Warning, "cycle",
                "Der aktive Graph enthält einen Zyklus — das kann zu Endlos- oder Nie-Ausführung führen."));
        }

        return findings;
    }

    private static string Label(WorkflowNode n) => string.IsNullOrWhiteSpace(n.Data.Label) ? n.Id : n.Data.Label!;

    /// <summary>BFS from every (active trigger) root, following only the active edges.</summary>
    private static HashSet<string> ReachableFromRoots(WorkflowDefinitionDocument doc)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var r in doc.RootNodes)
            if (seen.Add(r.Id)) queue.Enqueue(r.Id);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!doc.Adjacency.TryGetValue(id, out var nexts)) continue;
            foreach (var t in nexts)
                if (seen.Add(t)) queue.Enqueue(t);
        }
        return seen;
    }

    /// <summary>DFS cycle detection over the active edges (white/gray/black node coloring).</summary>
    private static bool HasCycle(WorkflowDefinitionDocument doc)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0=white 1=gray 2=black
        foreach (var n in doc.Nodes) state[n.Id] = 0;

        bool Visit(string id)
        {
            state[id] = 1;
            if (doc.Adjacency.TryGetValue(id, out var nexts))
            {
                foreach (var t in nexts)
                {
                    if (!state.TryGetValue(t, out var s)) continue;
                    if (s == 1) return true;            // edge back to a node still on the stack → cycle
                    if (s == 0 && Visit(t)) return true;
                }
            }
            state[id] = 2;
            return false;
        }

        foreach (var n in doc.Nodes)
            if (state[n.Id] == 0 && Visit(n.Id)) return true;
        return false;
    }
}
