using System.Collections.Concurrent;
using NodePilot.Core.Models;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Engine.Execution;

internal sealed record CompiledWorkflowDefinition(
    IReadOnlyList<WorkflowNode> Nodes,
    IReadOnlyList<WorkflowEdge> Edges,
    IReadOnlyDictionary<string, WorkflowNode> NodesById,
    IReadOnlyDictionary<string, string> OutputNameByStepId,
    IReadOnlyDictionary<string, string> OutputVariableToStepId,
    Dictionary<string, List<string>> Adjacency,
    Dictionary<string, List<string>> ReverseAdjacency,
    IReadOnlyDictionary<string, List<WorkflowEdge>> IncomingEdgesByTarget,
    IReadOnlyDictionary<(string Source, string Target), WorkflowEdge> ActiveEdgeByEndpoints,
    IReadOnlyList<WorkflowNode> RootNodes,
    IReadOnlyDictionary<string, RetryPolicy> RetryPolicies);

internal static class WorkflowDefinitionCache
{
    private const int MaxEntries = 512;
    private static readonly ConcurrentDictionary<Guid, CacheEntry> Cache = new();
    private static readonly ConcurrentDictionary<Guid, object> Locks = new();

    internal static CompiledWorkflowDefinition GetOrCompile(Workflow workflow)
    {
        if (Cache.TryGetValue(workflow.Id, out var cached) && cached.Matches(workflow))
            return cached.Definition;

        var gate = Locks.GetOrAdd(workflow.Id, _ => new object());
        lock (gate)
        {
            if (Cache.TryGetValue(workflow.Id, out cached) && cached.Matches(workflow))
                return cached.Definition;

            var compiled = Compile(workflow.DefinitionJson);
            Cache[workflow.Id] = new CacheEntry(
                workflow.Version,
                workflow.UpdatedAt,
                workflow.DefinitionJson.Length,
                compiled);

            if (Cache.Count > MaxEntries)
                TrimCache(workflow.Id);

            return compiled;
        }
    }

    internal static void ClearForTests()
    {
        Cache.Clear();
        Locks.Clear();
    }

    private static void TrimCache(Guid keepWorkflowId)
    {
        foreach (var id in Cache.Keys)
        {
            if (id == keepWorkflowId) continue;
            Cache.TryRemove(id, out _);
            Locks.TryRemove(id, out _);
            if (Cache.Count <= MaxEntries / 2) break;
        }
    }

    private static CompiledWorkflowDefinition Compile(string definitionJson)
    {
        var definition = WorkflowDefinitionDocument.Parse(definitionJson);
        var retryPolicies = definition.Nodes.ToDictionary(
            node => node.Id,
            node => RetryPolicy.Parse(node.Data.Config),
            StringComparer.Ordinal);

        return new CompiledWorkflowDefinition(
            definition.Nodes,
            definition.Edges,
            definition.NodesById,
            definition.OutputNameByStepId,
            definition.OutputVariableToStepId,
            definition.Adjacency,
            definition.ReverseAdjacency,
            definition.IncomingEdgesByTarget,
            definition.ActiveEdgeByEndpoints,
            definition.RootNodes,
            retryPolicies);
    }

    private sealed record CacheEntry(
        int Version,
        DateTime UpdatedAt,
        int DefinitionLength,
        CompiledWorkflowDefinition Definition)
    {
        public bool Matches(Workflow workflow)
            => Version == workflow.Version
               && UpdatedAt == workflow.UpdatedAt
               && DefinitionLength == workflow.DefinitionJson.Length;
    }
}
