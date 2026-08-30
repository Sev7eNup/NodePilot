using System.Text.Json;
using System.Text.RegularExpressions;
using NodePilot.Core.Activities;
using NodePilot.Core.Models;

namespace NodePilot.Core.WorkflowDefinitions;

/// <summary>
/// Data-bus reasoning over a definition: which <c>{{…}}</c> references are available at a node,
/// and which references in the workflow will not resolve under the contract guarantee (only the
/// <c>output</c>/<c>error</c>/<c>success</c>/<c>param.X</c> tails plus
/// <c>globals.*</c>/<c>manual.*</c>
/// resolve; anything else stays a literal).
///
/// <para>Static analysis over an unsaved definition. It is not
/// <c>NodePilot.Engine.VariableResolver</c>, which substitutes values during a real run; the
/// distinct name keeps the two apart in files that use both.</para>
/// </summary>
public static class WorkflowDataBusAnalyzer
{
    private static readonly Regex TemplateRx = new(@"\{\{\s*(.*?)\s*\}\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex PsAssignRx = new(@"\$([A-Za-z_][A-Za-z0-9_]*)\s*=", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public sealed record AvailableVariables(string NodeId, IReadOnlyList<string> Upstream, IReadOnlyList<string> RunLevel);

    public sealed record UnresolvedRef(string NodeId, string Reference, string Code, string Reason);

    public static AvailableVariables Available(JsonElement definition, string nodeId)
    {
        var doc = WorkflowDefinitionDocument.FromJsonElement(definition);
        if (!doc.NodesById.ContainsKey(nodeId))
            throw new ArgumentException($"No node '{nodeId}' in this definition.");

        var upstream = new List<string>();
        foreach (var ancId in doc.FindAncestorNodeIds(nodeId))
        {
            var node = doc.NodesById[ancId];
            var name = doc.OutputNameByStepId[ancId];
            upstream.AddRange(DescribeNode(node, name)); // triggers included (e.g. {{hook.param.webhookBody}})
        }

        // Run-level: manual.* from every manualTrigger's declared parameters (available anywhere in
        // the run).
        var runLevel = new List<string>();
        foreach (var node in doc.Nodes.Where(n => string.Equals(n.Type, "manualTrigger", StringComparison.Ordinal)))
            foreach (var p in ManualParams(node))
                runLevel.Add($"{{{{manual.{p}}}}}");

        return new AvailableVariables(nodeId, upstream.Distinct().ToList(), runLevel.Distinct().ToList());
    }

    public static IReadOnlyList<UnresolvedRef> FindUnresolved(JsonElement definition)
    {
        var doc = WorkflowDefinitionDocument.FromJsonElement(definition);

        // Heads that resolve to a step: every node id AND every output-variable alias.
        var stepNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in doc.Nodes) stepNames.Add(n.Id);
        foreach (var kv in doc.OutputNameByStepId) stepNames.Add(kv.Value);

        var unresolved = new List<UnresolvedRef>();
        foreach (var node in doc.Nodes)
        {
            foreach (var raw in CollectStrings(node.Data.Config))
            {
                foreach (Match m in TemplateRx.Matches(raw))
                {
                    var inner = m.Groups[1].Value.Trim();
                    if (inner.Length == 0) continue;
                    var dot = inner.IndexOf('.');
                    var head = dot < 0 ? inner : inner[..dot];
                    var tail = dot < 0 ? "" : inner[(dot + 1)..];

                    if (head.Equals("globals", StringComparison.OrdinalIgnoreCase) || head.Equals("manual", StringComparison.OrdinalIgnoreCase))
                        continue; // run-level namespaces — name validated at runtime

                    if (!stepNames.Contains(head))
                    {
                        unresolved.Add(new UnresolvedRef(node.Id, m.Value, "unknown-template-ref",
                            $"Unknown variable '{head}' — not a step output name/alias, nor globals/manual."));
                        continue;
                    }

                    var validTail = tail is "output" or "error" or "success" || tail.StartsWith("param.", StringComparison.Ordinal);
                    if (!validTail)
                    {
                        var hint = string.IsNullOrEmpty(tail)
                            ? "add a tail: .output / .error / .success / .param.X"
                            : $"did you mean {{{{{head}.param.{tail}}}}}?";
                        unresolved.Add(new UnresolvedRef(node.Id, m.Value, "invalid-template-tail",
                            $"Tail '.{tail}' won't resolve (stays literal) — only output/error/success/param.X resolve; {hint}"));
                    }
                }
            }
        }
        return unresolved;
    }

    /// <summary>
    /// Every parameter name a node writes to the data bus: the catalog's static outputs plus the
    /// ones that only exist once the node is configured — a runScript's script-scope variables, a
    /// webhook's field mappings, returnData's keys, a wmiQuery's captured properties.
    ///
    /// <para>Public because "what does this step publish" is not only a designer question. The
    /// SCOrch importer needs it to tell a reference that will resolve from one that will not, and
    /// the static catalog alone answers that a runScript publishes nothing but its exit code —
    /// which would flag every reference to a value its script actually produces.</para>
    ///
    /// <para>An empty result means "not knowable from the definition" (a custom activity, a
    /// wmiQuery without captureProperties), not "publishes nothing" — callers should not treat it
    /// as grounds to reject a reference.</para>
    /// </summary>
    public static IReadOnlyList<string> PublishedParameters(WorkflowNode node)
    {
        var names = new List<string>(DynamicParams(node));
        if (ActivityCatalog.TryGet(node.Type, out var desc) && desc is not null)
            names.AddRange(desc.OutputParameters.Select(p => p.Name));
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Only the names this node publishes because of how it is configured — the variables a
    /// script assigns, the keys a returnData carries, the properties a wmiQuery captures.
    ///
    /// <para>Excludes the static catalog outputs every instance of a type emits. Those collide by
    /// construction (two runScript steps both publish <c>exitCode</c>), so reporting them as an
    /// authoring problem would fire on nearly every workflow and drown the cases that are one.</para>
    /// </summary>
    public static IReadOnlyList<string> AuthoredParameters(WorkflowNode node) =>
        DynamicParams(node).Distinct(StringComparer.Ordinal).ToList();

    // Mirrors the FE describeNodeOutputs: the full set of {{name.…}} expressions a node exposes.
    private static IEnumerable<string> DescribeNode(WorkflowNode node, string name)
    {
        var refs = new List<string>
        {
            $"{{{{{name}.output}}}}",
            $"{{{{{name}.error}}}}",
            $"{{{{{name}.success}}}}",
        };
        foreach (var p in PublishedParameters(node)) refs.Add($"{{{{{name}.param.{p}}}}}");
        return refs.Distinct();
    }

    private static IEnumerable<string> DynamicParams(WorkflowNode node) => node.Type switch
    {
        "manualTrigger" => ManualParams(node),
        "webhookTrigger" => WebhookFieldMappingParams(node),
        "returnData" => ReturnDataKeys(node),
        "runScript" => RunScriptVars(node),
        "registryOperation" => RegistryParams(node),
        "wmiQuery" => WmiCaptureParams(node),
        _ => [],
    };

    // Mirrors FE addWebhookFieldMappingParams: user-named JSONPath extractions
    // (config.fieldMappings[{name,path}]) the webhook controller injects as params.
    private static IEnumerable<string> WebhookFieldMappingParams(WorkflowNode node)
    {
        if (node.Data.Config.ValueKind != JsonValueKind.Object
            || !node.Data.Config.TryGetProperty("fieldMappings", out var mappings)
            || mappings.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var m in mappings.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            if (m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(n.GetString()))
            {
                yield return n.GetString()!;
            }
        }
    }

    /// <summary>
    /// Names the runtime never publishes, so the picker must not offer them either. Seeded from
    /// the same contract the script wrapper enforces
    /// (<see cref="Activities.PowerShellReservedVariables"/>) — an assignment to a reserved name
    /// changes engine state rather than producing an output.
    /// </summary>
    private static readonly HashSet<string> RunScriptIgnored =
        new(Activities.PowerShellReservedVariables.All, StringComparer.OrdinalIgnoreCase)
        {
            "Params",
            "exitCode", // comes from the static catalog
        };

    private static IEnumerable<string> RunScriptVars(WorkflowNode node)
    {
        if (!TryGetString(node.Data.Config, "script", out var script)) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in PsAssignRx.Matches(script!))
        {
            var v = m.Groups[1].Value;
            if (RunScriptIgnored.Contains(v)
                || v.StartsWith(Activities.PowerShellReservedVariables.InternalPrefix, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(v))
            {
                continue;
            }
            yield return v;
        }
    }

    private static IEnumerable<string> ReturnDataKeys(WorkflowNode node)
    {
        if (node.Data.Config.ValueKind == JsonValueKind.Object
            && node.Data.Config.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            foreach (var p in data.EnumerateObject())
                if (!string.IsNullOrEmpty(p.Name)) yield return p.Name;
    }

    private static IEnumerable<string> WmiCaptureParams(WorkflowNode node)
    {
        if (node.Data.Config.ValueKind != JsonValueKind.Object
            || !node.Data.Config.TryGetProperty("captureProperties", out var caps) || caps.ValueKind != JsonValueKind.Array)
            yield break;
        yield return "count";
        foreach (var c in caps.EnumerateArray())
            if (c.ValueKind == JsonValueKind.String && !string.Equals(c.GetString(), "count", StringComparison.OrdinalIgnoreCase))
                yield return c.GetString()!;
    }

    // Mirrors FE addRegistryParams: outputs depend on operation (+ valueName for read).
    private static IEnumerable<string> RegistryParams(WorkflowNode node)
    {
        var op = (TryGetString(node.Data.Config, "operation", out var o) ? o! : "read").ToLowerInvariant();
        var hasValueName = TryGetString(node.Data.Config, "valueName", out var vn) && !string.IsNullOrEmpty(vn);
        return op switch
        {
            "read" => hasValueName ? ["value", "type"] : ["values", "count"],
            "listvalues" => ["values", "count"],
            "listsubkeys" => ["subKeys", "count"],
            "exists" => ["exists"],
            "createkey" => ["created"],
            "write" => ["type"],
            _ => [],
        };
    }

    private static IEnumerable<string> ManualParams(WorkflowNode node)
    {
        if (node.Data.Config.ValueKind == JsonValueKind.Object
            && node.Data.Config.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Array)
            foreach (var p in ps.EnumerateArray())
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                    yield return nm.GetString()!;
    }

    private static bool TryGetString(JsonElement obj, string name, out string? value)
    {
        value = null;
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString();
            return value is not null;
        }
        return false;
    }

    private static IEnumerable<string> CollectStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                    foreach (var s in CollectStrings(p.Value)) yield return s;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var s in CollectStrings(item)) yield return s;
                break;
        }
    }
}
