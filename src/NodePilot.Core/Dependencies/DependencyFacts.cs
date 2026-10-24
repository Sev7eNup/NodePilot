using System.Text.Json;
using System.Text.RegularExpressions;
using NodePilot.Core.Activities;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Core.Dependencies;

public sealed record DependencyReference(string Kind, string Value, string Relation, string? NodeId,
    string Label, string Field, bool Disabled, string? DefaultParameter = null);

public sealed record DependencyBinding(string? Target, string? Credential, string NodeId, string Label,
    string Field, bool Disabled, bool IsAgent, bool ServiceIdentity, string? CustomId = null,
    string? TargetField = null, string? CredentialField = null);

public sealed record DependencyCustomUse(string Id, string? Key, string NodeId, string Label,
    bool Disabled, IReadOnlyList<string> SuppliedInputs);

/// <summary>Definition-local references. Raw values stay inside the server; API evidence contains only locations.</summary>
public sealed record DependencyFacts(bool Valid, IReadOnlyList<DependencyReference> References,
    IReadOnlyList<DependencyBinding> Bindings, IReadOnlyList<DependencyCustomUse> CustomUses,
    IReadOnlyList<string> Limitations)
{
    public static DependencyFacts Invalid { get; } = new(false, [], [], [], ["invalidDefinition"]);
    private static readonly Regex Globals = new(@"\{\{globals\.([A-Za-z0-9_\-]+)\}\}",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static DependencyFacts Extract(string? json)
    {
        if (!WorkflowDefinitionDocument.TryParse(json, out var doc) || doc is null) return Invalid;
        return Extract(doc);
    }

    public static DependencyFacts Extract(WorkflowDefinitionDocument doc)
    {
        var references = new List<DependencyReference>();
        var bindings = new List<DependencyBinding>();
        var customUses = new List<DependencyCustomUse>();
        var limitations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in doc.Nodes)
        {
            var config = node.Data.Config;
            var label = string.IsNullOrWhiteSpace(node.Data.Label) ? node.Type : node.Data.Label;
            void Scan(JsonElement value, string field) => ScanGlobals(value, field,
                (name, path) => references.Add(new("variable", name, "usesVariable", node.Id, label, path, node.Data.Disabled)));
            void ScanText(string? value, string field) => ScanGlobals(value, field,
                (name, path) => references.Add(new("variable", name, "usesVariable", node.Id, label, path, node.Data.Disabled)));

            var custom = CustomActivityType.IsCustomType(node.Type);
            ActivityCatalog.TryGet(node.Type, out var descriptor);
            if (custom)
            {
                customUses.Add(new(Text(config, "__customDefinitionId") ?? "", Text(config, "__customKey")
                    ?? node.Type[CustomActivityType.Prefix.Length..], node.Id, label, node.Data.Disabled,
                    config.ValueKind == JsonValueKind.Object
                        ? config.EnumerateObject().Where(p => p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                            .Select(p => p.Name).ToArray() : []));
            }
            if (descriptor?.IsRemote == true || custom)
                bindings.Add(new(node.Data.TargetMachineRaw, node.Data.CredentialRaw, node.Id, label,
                    "data", node.Data.Disabled, false, false, custom ? Text(config, "__customDefinitionId") ?? "" : null));
            if (descriptor is not null || custom)
            {
                ScanText(node.Data.TargetMachineRaw, "data.targetMachineId");
                ScanText(node.Data.CredentialRaw, "data.credentialId");
                ScanText(node.Data.BreakpointCondition, "data.breakpointCondition");
                if (config.ValueKind == JsonValueKind.Object)
                {
                    var knownKeys = ActivityConfigReference.TryGet(node.Type)?.ConfigKeys.Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
                    foreach (var property in config.EnumerateObject())
                        if (!property.Name.StartsWith("__", StringComparison.Ordinal) && (custom || knownKeys?.Contains(property.Name) == true))
                            Scan(property.Value, "data.config." + property.Name);
                }
            }
            else if (node.Type is not ("stickyNote" or "group")) limitations.Add("unsupportedActivity");

            var callKey = node.Type switch { "startWorkflow" => "workflowNameOrId", "forEach" => "childWorkflowNameOrId", _ => null };
            if (callKey is not null)
                references.Add(new("workflow", Text(config, callKey) ?? "", "calls", node.Id, label,
                    "data.config." + callKey, node.Data.Disabled));

            if (node.Type is "aiAgent" or "aiAgentTeam")
            {
                void Agent(JsonElement agent, string path)
                {
                    bindings.Add(new(Text(agent, "targetMachineId") ?? node.Data.TargetMachineRaw,
                        Text(agent, "credentialId") ?? node.Data.CredentialRaw, node.Id, label, path,
                        node.Data.Disabled, true, Bool(agent, "useServiceIdentity"),
                        TargetField: Text(agent, "targetMachineId") is null ? "data.targetMachineId" : path + ".targetMachineId",
                        CredentialField: Text(agent, "credentialId") is null ? "data.credentialId" : path + ".credentialId"));
                    if (agent.TryGetProperty("skillIds", out var skills) && skills.ValueKind == JsonValueKind.Array && skills.GetArrayLength() > 0)
                        limitations.Add("agentResources");
                    if (!agent.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array) return;
                    var index = 0;
                    foreach (var tool in tools.EnumerateArray())
                    {
                        var name = Text(tool, "name");
                        if (name == "workflow_run" && tool.TryGetProperty("workflowIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
                            foreach (var id in ids.EnumerateArray())
                                if (id.ValueKind == JsonValueKind.String)
                                    references.Add(new("workflow", id.GetString() ?? "", "mayCall", node.Id, label,
                                        $"{path}.tools[{index}].workflowIds", node.Data.Disabled));
                        if (name == "mcp") limitations.Add("agentResources");
                        index++;
                    }
                }
                if (config.ValueKind == JsonValueKind.Object)
                {
                    if (config.TryGetProperty("agent", out var agent) && agent.ValueKind == JsonValueKind.Object)
                        Agent(agent, "data.config.agent");
                    if (config.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
                    {
                        var index = 0;
                        foreach (var member in members.EnumerateArray())
                        {
                            if (member.ValueKind == JsonValueKind.Object) Agent(member, $"data.config.members[{index}]");
                            index++;
                        }
                    }
                }
            }
            if (node.Type is "runScript" or "restApi" or "sql" or "aiAgent" or "aiAgentTeam") limitations.Add("staticOnly");
        }
        foreach (var edge in doc.Edges)
        {
            void Add(string name, string field) => references.Add(new("variable", name, "usesVariable", edge.Source,
                doc.FindNode(edge.Source)?.Data.Label ?? edge.Source, field, edge.Disabled));
            ScanGlobals(edge.Condition, $"edges[{edge.Id}].condition", Add);
            if (edge.ConditionExpression is { } expression) ScanGlobals(expression, $"edges[{edge.Id}].conditionExpression", Add);
        }
        return new(true, references.Distinct().ToArray(), bindings, customUses, limitations.Order().ToArray());
    }

    public static DependencyFacts ExtractCustom(string script, string inputsJson)
    {
        var references = new List<DependencyReference>();
        ScanGlobals(script, "scriptTemplate", (name, path) => references.Add(new("variable", name, "usesVariable", null, "", path, false)));
        try
        {
            using var inputs = JsonDocument.Parse(inputsJson);
            if (inputs.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var input in inputs.RootElement.EnumerateArray())
                {
                    var name = Text(input, "name");
                    if (name is not null)
                        ScanGlobals(Text(input, "default"), "inputParameters.default", (global, path) =>
                            references.Add(new("variable", global, "usesDefaultVariable", null, name, path, false, name)));
                }
        }
        catch (JsonException) { return new(false, references, [], [], ["invalidDefinition"]); }
        return new(true, references.Distinct().ToArray(), [], [], ["staticOnly"]);
    }

    private static void ScanGlobals(JsonElement value, string path, Action<string, string> add)
    {
        if (value.ValueKind == JsonValueKind.String) ScanGlobals(value.GetString(), path, add);
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var child in value.EnumerateArray()) ScanGlobals(child, $"{path}[{i++}]", add);
        }
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var child in value.EnumerateObject()) ScanGlobals(child.Value, path + "." + child.Name, add);
    }

    private static void ScanGlobals(string? value, string path, Action<string, string> add)
    {
        if (string.IsNullOrEmpty(value)) return;
        foreach (Match match in Globals.Matches(value)) add(match.Groups[1].Value, path);
    }

    private static string? Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Bool(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
}
