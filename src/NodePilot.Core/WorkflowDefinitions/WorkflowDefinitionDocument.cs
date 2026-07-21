using System.Text.Json;
using NodePilot.Core.Activities;
using NodePilot.Core.Models;

namespace NodePilot.Core.WorkflowDefinitions;

public sealed record WorkflowDefinitionMetadata(int ActivityCount, IReadOnlyList<string> TriggerTypes);

public sealed record WorkflowTriggerDescriptor(
    string NodeId,
    string ActivityType,
    JsonElement Config,
    string Hash,
    bool IsExternal,
    bool IsManual);

public sealed record WorkflowDefinitionDocument(
    IReadOnlyList<WorkflowNode> Nodes,
    IReadOnlyList<WorkflowEdge> Edges,
    IReadOnlyDictionary<string, WorkflowNode> NodesById,
    IReadOnlyDictionary<string, string> OutputNameByStepId,
    IReadOnlyDictionary<string, string> OutputVariableToStepId,
    IReadOnlySet<string> DisabledNodeIds,
    IReadOnlyList<WorkflowEdge> ActiveEdges,
    Dictionary<string, List<string>> Adjacency,
    Dictionary<string, List<string>> ReverseAdjacency,
    IReadOnlyDictionary<string, List<WorkflowEdge>> IncomingEdgesByTarget,
    IReadOnlyDictionary<(string Source, string Target), WorkflowEdge> ActiveEdgeByEndpoints,
    IReadOnlyList<WorkflowNode> RootNodes,
    IReadOnlyList<WorkflowTriggerDescriptor> TriggerDescriptors,
    WorkflowDefinitionMetadata Metadata)
{
    public WorkflowNode? FindNode(string stepId)
        => NodesById.TryGetValue(stepId, out var node) ? node : null;

    public WorkflowTriggerDescriptor? FindFirstTrigger(string activityType)
        => TriggerDescriptors.FirstOrDefault(d => string.Equals(d.ActivityType, activityType, StringComparison.Ordinal));

    public HashSet<string> FindAncestorNodeIds(string stepId, bool includeDisabledEdges = false)
    {
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        if (!NodesById.ContainsKey(stepId))
            return ancestors;

        var edges = includeDisabledEdges ? Edges : ActiveEdges;
        var byTarget = edges
            .GroupBy(e => e.Target)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var queue = new Queue<string>();
        queue.Enqueue(stepId);
        var seen = new HashSet<string>(StringComparer.Ordinal) { stepId };

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!byTarget.TryGetValue(current, out var incoming))
                continue;

            foreach (var edge in incoming)
            {
                if (!seen.Add(edge.Source))
                    continue;

                ancestors.Add(edge.Source);
                queue.Enqueue(edge.Source);
            }
        }

        return ancestors;
    }

    public static WorkflowDefinitionDocument Parse(string definitionJson)
    {
        using var document = JsonDocument.Parse(definitionJson);
        return FromJsonElement(document.RootElement);
    }

    public static bool TryParse(string? definitionJson, out WorkflowDefinitionDocument? definition)
    {
        definition = null;
        if (string.IsNullOrWhiteSpace(definitionJson))
            return false;

        try
        {
            definition = Parse(definitionJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    public static WorkflowDefinitionDocument FromJsonElement(JsonElement definition)
    {
        var nodes = WorkflowDefinitionParser.ParseNodes(definition);
        var edges = WorkflowDefinitionParser.ParseEdges(definition);
        var nodesById = BuildNodesById(nodes);
        var outputNameByStepId = BuildOutputNameByStepId(nodes);
        var outputVariableToStepId = BuildOutputVariableAliasMap(nodes);

        var disabledNodeIds = nodes
            .Where(n => n.Data.Disabled)
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);

        var activeEdges = edges
            .Where(e => !e.Disabled
                        && !disabledNodeIds.Contains(e.Source)
                        && !disabledNodeIds.Contains(e.Target))
            .ToList();

        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var reverseAdjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var incomingEdgesByTarget = new Dictionary<string, List<WorkflowEdge>>(StringComparer.Ordinal);
        var activeEdgeByEndpoints = new Dictionary<(string Source, string Target), WorkflowEdge>();

        foreach (var node in nodes)
        {
            adjacency[node.Id] = [];
            reverseAdjacency[node.Id] = [];
            incomingEdgesByTarget[node.Id] = [];
        }

        foreach (var edge in activeEdges)
        {
            if (!adjacency.ContainsKey(edge.Source) || !reverseAdjacency.ContainsKey(edge.Target))
                continue;

            adjacency[edge.Source].Add(edge.Target);
            reverseAdjacency[edge.Target].Add(edge.Source);
            incomingEdgesByTarget[edge.Target].Add(edge);
            activeEdgeByEndpoints.TryAdd((edge.Source, edge.Target), edge);
        }

        // Roots are EXCLUSIVELY trigger-type entry activities — never a plain activity. A workflow
        // without an (enabled) trigger has no root → no node executes (the engine fails it with a
        // clear "no start activity" message). Disconnected/orphan activities are never roots and
        // therefore never run. There is intentionally NO inDegree==0 fallback.
        var rootNodes = nodes
            .Where(n => ActivityCatalog.TriggerTypes.Contains(n.Type) && !disabledNodeIds.Contains(n.Id))
            .ToList();

        var triggerDescriptors = BuildTriggerDescriptors(nodes, disabledNodeIds);
        var metadata = BuildMetadata(nodes, disabledNodeIds);

        return new WorkflowDefinitionDocument(
            nodes,
            edges,
            nodesById,
            outputNameByStepId,
            outputVariableToStepId,
            disabledNodeIds,
            activeEdges,
            adjacency,
            reverseAdjacency,
            incomingEdgesByTarget,
            activeEdgeByEndpoints,
            rootNodes,
            triggerDescriptors,
            metadata);
    }

    public static Dictionary<string, WorkflowNode> BuildNodesById(IReadOnlyList<WorkflowNode> allNodes)
    {
        var dict = new Dictionary<string, WorkflowNode>(allNodes.Count, StringComparer.Ordinal);
        foreach (var node in allNodes)
            dict[node.Id] = node;
        return dict;
    }

    public static Dictionary<string, string> BuildOutputNameByStepId(IReadOnlyList<WorkflowNode> allNodes)
    {
        var dict = new Dictionary<string, string>(allNodes.Count, StringComparer.Ordinal);
        foreach (var node in allNodes)
            dict[node.Id] = node.Data.OutputVariable ?? node.Id;
        return dict;
    }

    public static Dictionary<string, string> BuildOutputVariableAliasMap(IReadOnlyList<WorkflowNode> allNodes)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in allNodes)
        {
            if (node.Data.OutputVariable is { Length: > 0 } variable && variable != node.Id)
                dict[variable] = node.Id;
        }

        return dict;
    }

    private static IReadOnlyList<WorkflowTriggerDescriptor> BuildTriggerDescriptors(
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlySet<string> disabledNodeIds)
    {
        var descriptors = new List<WorkflowTriggerDescriptor>();
        foreach (var node in nodes)
        {
            if (disabledNodeIds.Contains(node.Id))
                continue;
            if (!ActivityCatalog.TryGet(node.Type, out var activity) || activity is null || !activity.IsTrigger)
                continue;

            var config = node.Data.Config.ValueKind == JsonValueKind.Undefined
                ? default
                : node.Data.Config.Clone();
            var hash = node.Type + ":" + (config.ValueKind == JsonValueKind.Undefined ? "" : config.GetRawText());
            descriptors.Add(new WorkflowTriggerDescriptor(
                node.Id,
                node.Type,
                config,
                hash,
                activity.IsExternalTrigger,
                string.Equals(node.Type, "manualTrigger", StringComparison.Ordinal)));
        }

        return descriptors;
    }

    private static WorkflowDefinitionMetadata BuildMetadata(
        IReadOnlyList<WorkflowNode> nodes,
        IReadOnlySet<string> disabledNodeIds)
    {
        var activityCount = 0;
        var triggers = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            if (disabledNodeIds.Contains(node.Id) || string.IsNullOrEmpty(node.Type))
                continue;

            if (ActivityCatalog.TryGet(node.Type, out var activity) && activity is { IsTrigger: true })
                triggers.Add(node.Type);
            else
                activityCount++;
        }

        return new WorkflowDefinitionMetadata(activityCount, triggers.ToList());
    }
}
