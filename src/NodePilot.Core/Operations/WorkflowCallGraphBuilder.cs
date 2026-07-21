using System.Text.Json;
using System.Text.RegularExpressions;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Core.Operations;

/// <summary>
/// How a <c>startWorkflow</c>/<c>forEach</c> child-workflow reference resolved against the
/// set of workflows the builder was given.
/// </summary>
public enum WorkflowRefStatus
{
    /// <summary>Reference resolved to exactly one workflow (by id, or by a unique name).</summary>
    Resolved = 0,
    /// <summary>Reference is a <c>{{...}}</c> data-bus template — only known at runtime.</summary>
    Dynamic = 1,
    /// <summary>Reference matched no workflow in the provided set (deleted, or out of RBAC scope).</summary>
    Unresolved = 2,
    /// <summary>Reference is a name that matches more than one workflow (workflow names are not unique).</summary>
    Ambiguous = 3,
}

/// <summary>Input row for <see cref="WorkflowCallGraphBuilder"/>: the minimum a workflow needs to derive call edges.</summary>
public sealed record WorkflowCallGraphInput(Guid Id, string Name, string DefinitionJson);

/// <summary>
/// A derived call relationship: workflow <see cref="SourceWorkflowId"/> invokes a child workflow
/// via a <c>startWorkflow</c> or <c>forEach</c> node. <see cref="TargetWorkflowId"/> is set only
/// when <see cref="RefStatus"/> is <see cref="WorkflowRefStatus.Resolved"/>; otherwise the edge is
/// rendered as a dangling/dynamic/warning edge keyed off <see cref="RawRef"/>.
/// </summary>
public sealed record WorkflowCallEdge(
    Guid SourceWorkflowId,
    Guid? TargetWorkflowId,
    string Kind,
    WorkflowRefStatus RefStatus,
    string RawRef,
    int CallCount);

/// <summary>
/// Pure derivation of the workflow-to-workflow call graph for the NOC / operations view.
/// Reads each workflow's <c>DefinitionJson</c> via <see cref="WorkflowDefinitionDocument"/> and
/// extracts edges from <c>startWorkflow.config.workflowNameOrId</c> and
/// <c>forEach.config.childWorkflowNameOrId</c>, applying the same name-or-id resolution rule the
/// frontend uses (GUID → by id, else by case-insensitive name; <c>{{...}}</c> stays dynamic).
/// <para>
/// The builder only resolves against the workflows it is handed, so the caller controls scope:
/// passing an RBAC-filtered set means a reference to a workflow the caller cannot see resolves to
/// <see cref="WorkflowRefStatus.Unresolved"/> (existence is not leaked). Duplicate references to the
/// same target within one source are collapsed into a single edge with <see cref="WorkflowCallEdge.CallCount"/>.
/// </para>
/// </summary>
public static class WorkflowCallGraphBuilder
{
    private static readonly Regex GuidPattern = new(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled);

    // activityType -> the config key that carries the child-workflow reference.
    private static readonly (string Type, string ConfigKey)[] CallSites =
    [
        ("startWorkflow", "workflowNameOrId"),
        ("forEach", "childWorkflowNameOrId"),
    ];

    public static IReadOnlyList<WorkflowCallEdge> Build(IReadOnlyCollection<WorkflowCallGraphInput> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);

        var byId = new Dictionary<Guid, WorkflowCallGraphInput>();
        var byName = new Dictionary<string, List<WorkflowCallGraphInput>>(StringComparer.OrdinalIgnoreCase);
        foreach (var wf in workflows)
        {
            byId[wf.Id] = wf;
            if (string.IsNullOrWhiteSpace(wf.Name))
                continue;
            if (!byName.TryGetValue(wf.Name, out var list))
                byName[wf.Name] = list = [];
            list.Add(wf);
        }

        // (source, dedupKey, kind, status) -> aggregate. dedupKey is the resolved target id when
        // resolved, otherwise the raw ref text, so two startWorkflow nodes pointing at the same
        // child collapse to one edge with CallCount=2, while two distinct dynamic refs stay separate.
        var acc = new Dictionary<(Guid Source, string DedupKey, string Kind, WorkflowRefStatus Status), (Guid? Target, string Raw, int Count)>();

        foreach (var wf in workflows)
        {
            if (!WorkflowDefinitionDocument.TryParse(wf.DefinitionJson, out var doc) || doc is null)
                continue;

            foreach (var node in doc.Nodes)
            {
                foreach (var (type, key) in CallSites)
                {
                    if (!string.Equals(node.Type, type, StringComparison.Ordinal))
                        continue;

                    var raw = ReadConfigString(node.Data.Config, key);
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    raw = raw.Trim();
                    var (status, target) = Resolve(raw, byId, byName);
                    var dedupKey = status == WorkflowRefStatus.Resolved && target.HasValue
                        ? target.Value.ToString()
                        : raw;

                    var k = (wf.Id, dedupKey, type, status);
                    acc[k] = acc.TryGetValue(k, out var existing)
                        ? (existing.Target, existing.Raw, existing.Count + 1)
                        : (target, raw, 1);
                }
            }
        }

        return acc
            .Select(kv => new WorkflowCallEdge(
                kv.Key.Source, kv.Value.Target, kv.Key.Kind, kv.Key.Status, kv.Value.Raw, kv.Value.Count))
            .ToList();
    }

    private static (WorkflowRefStatus Status, Guid? Target) Resolve(
        string raw,
        Dictionary<Guid, WorkflowCallGraphInput> byId,
        Dictionary<string, List<WorkflowCallGraphInput>> byName)
    {
        if (raw.StartsWith("{{", StringComparison.Ordinal))
            return (WorkflowRefStatus.Dynamic, null);

        if (GuidPattern.IsMatch(raw) && Guid.TryParse(raw, out var id))
            return byId.ContainsKey(id) ? (WorkflowRefStatus.Resolved, id) : (WorkflowRefStatus.Unresolved, null);

        if (byName.TryGetValue(raw, out var matches))
        {
            return matches.Count == 1
                ? (WorkflowRefStatus.Resolved, matches[0].Id)
                : (WorkflowRefStatus.Ambiguous, null);
        }

        return (WorkflowRefStatus.Unresolved, null);
    }

    private static string? ReadConfigString(JsonElement config, string key)
    {
        if (config.ValueKind != JsonValueKind.Object)
            return null;
        return config.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}
