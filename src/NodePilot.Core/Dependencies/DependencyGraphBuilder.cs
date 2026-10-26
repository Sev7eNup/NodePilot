using NodePilot.Core.Operations;

namespace NodePilot.Core.Dependencies;

/// <summary>Resolves metadata without credentials, script execution or network probes.</summary>
public static class DependencyGraphBuilder
{
    public static DependencyGraph Build(IReadOnlyList<DependencyResource> resources,
        IReadOnlyList<WorkflowCallGraphIdentity> workflowIdentities,
        IReadOnlyDictionary<Guid, DependencyFacts> workflowFacts,
        IReadOnlyList<DependencyMachine> machines, IReadOnlyList<DependencyCustom> customs,
        bool includeRestricted)
    {
        var visible = resources.ToDictionary(r => r.Key);
        var links = new Dictionary<(DependencyKey Source, DependencyKey Target, string Relation), List<DependencyLocation>>();
        var problems = new List<DependencyProblem>();
        var limits = new Dictionary<DependencyKey, IReadOnlyList<string>>();
        var customById = customs.ToDictionary(c => c.Id);
        var machinesById = machines.ToDictionary(m => m.Id);
        var globals = resources.Where(r => r.Kind == "variable").ToDictionary(r => r.Name, StringComparer.Ordinal);

        void Link(DependencyKey source, DependencyKey target, string relation, DependencyLocation location)
        {
            if (!visible.ContainsKey(target)) return;
            var key = (source, target, relation);
            if (!links.TryGetValue(key, out var locations)) links[key] = locations = [];
            if (!locations.Contains(location)) locations.Add(location);
        }
        DependencyLocation Location(DependencyResource source, string? node, string label, string field, bool disabled,
            string? via = null) => new(source.Key, node, label, field, disabled, source.Revision, via);
        void Problem(DependencyResource source, string kind, string status, DependencyLocation location)
            => problems.Add(new(source.Key, kind, status, location));

        foreach (var source in resources)
        {
            DependencyFacts? facts = source.Kind switch
            {
                "workflow" => workflowFacts.GetValueOrDefault(source.Id),
                "customActivity" => customById.GetValueOrDefault(source.Id)?.Facts,
                _ => null,
            };
            if (facts is null) continue;
            limits[source.Key] = facts.Limitations;
            if (!facts.Valid) Problem(source, source.Kind, "invalid", Location(source, null, source.Name, "definition", false));
            foreach (var reference in facts.References)
            {
                if (!includeRestricted && reference.Kind is "variable" or "credential") continue;
                var location = Location(source, reference.NodeId, reference.Label, reference.Field, reference.Disabled)
                    with { DefaultParameter = reference.DefaultParameter };
                if (reference.Value.Contains("{{", StringComparison.Ordinal)) { Problem(source, reference.Kind, "dynamic", location); continue; }
                DependencyKey? target = null;
                var status = "unresolved";
                if (reference.Kind == "workflow")
                {
                    var matches = Guid.TryParse(reference.Value, out var id)
                        ? workflowIdentities.Where(w => w.Id == id).ToArray()
                        : WorkflowIdentityResolution.Matches(workflowIdentities, reference.Value);
                    if (matches.Length == 1) target = new("workflow", matches[0].Id);
                    else if (matches.Length > 1 && matches.All(w => visible.ContainsKey(new("workflow", w.Id)))) status = "ambiguous";
                }
                if (reference.Kind == "variable" && globals.TryGetValue(reference.Value, out var variable)) target = variable.Key;
                if (target.HasValue && visible.ContainsKey(target.Value)) Link(source.Key, target.Value, reference.Relation, location);
                else Problem(source, reference.Kind, status, location);
            }
            foreach (var use in facts.CustomUses)
            {
                var location = Location(source, use.NodeId, use.Label, "data.config.__customDefinitionId", use.Disabled)
                    with { SuppliedInputs = use.SuppliedInputs };
                if (Guid.TryParse(use.Id, out var id) && customById.TryGetValue(id, out var custom))
                {
                    if (use.Key is not null && use.Key != custom.Key) Problem(source, "customActivity", "invalid", location);
                    else Link(source.Key, new("customActivity", id), "usesCustom", location);
                }
                else Problem(source, "customActivity", "unresolved", location);
            }
            foreach (var binding in facts.Bindings)
            {
                if (binding.CustomId is not null && (!Guid.TryParse(binding.CustomId, out var cid)
                    || !customById.TryGetValue(cid, out var custom) || !custom.RunsRemote)) continue;
                var targetLocation = Location(source, binding.NodeId, binding.Label, binding.TargetField ?? binding.Field + ".targetMachineId", binding.Disabled);
                var raw = binding.Target;
                DependencyMachine? machine = null;
                var dynamic = raw?.Contains("{{", StringComparison.Ordinal) == true;
                if (dynamic) Problem(source, "machine", "dynamic", targetLocation);
                else if (!string.IsNullOrWhiteSpace(raw))
                {
                    if (Guid.TryParse(raw, out var id)) machine = machinesById.GetValueOrDefault(id);
                    else
                    {
                        var candidates = machines.Where(m => m.Name == raw || m.Hostname == raw).ToArray();
                        if (candidates.Length == 1) machine = candidates[0];
                        else if (candidates.Length > 1) Problem(source, "machine", "ambiguous", targetLocation);
                    }
                    if (machine is null && !problems.Any(p => p.Location == targetLocation))
                        Problem(source, "machine", IsLocal(raw) ? "local" : Guid.TryParse(raw, out _) ? "unresolved" : "external", targetLocation);
                    if (machine is not null) Link(source.Key, new("machine", machine.Id), "usesMachine", targetLocation);
                }
                else Problem(source, "machine", "local", targetLocation);

                if (!includeRestricted || binding.ServiceIdentity) continue;
                var explicitCredential = !string.IsNullOrWhiteSpace(binding.Credential);
                var credentialLocation = Location(source, binding.NodeId, binding.Label,
                    explicitCredential ? binding.CredentialField ?? binding.Field + ".credentialId" : "defaultCredentialId", binding.Disabled,
                    explicitCredential ? null : machine?.Id.ToString());
                if (explicitCredential && binding.Credential!.Contains("{{", StringComparison.Ordinal))
                    Problem(source, "credential", "dynamic", credentialLocation);
                else if (explicitCredential && (binding.IsAgent || !string.IsNullOrWhiteSpace(raw)))
                {
                    if (Guid.TryParse(binding.Credential, out var id) && visible.ContainsKey(new("credential", id)))
                        Link(source.Key, new("credential", id), "usesCredential", credentialLocation);
                    else Problem(source, "credential", "unresolved", credentialLocation);
                }
                else if (!explicitCredential && machine?.CredentialId is { } inherited && visible.ContainsKey(new("credential", inherited)))
                    Link(source.Key, new("credential", inherited), "inheritsCredential", credentialLocation);
            }
        }
        if (includeRestricted)
            foreach (var machine in machines)
                if (machine.CredentialId is { } id && visible.TryGetValue(new("machine", machine.Id), out var source))
                    Link(source.Key, new("credential", id), "defaultCredential", Location(source, null, source.Name, "defaultCredentialId", false));
        return new(resources, links.OrderBy(kv => kv.Key.Source.Kind).ThenBy(kv => kv.Key.Source.Id)
            .ThenBy(kv => kv.Key.Target.Kind).ThenBy(kv => kv.Key.Target.Id).ThenBy(kv => kv.Key.Relation)
            .Select(kv => new DependencyLink(kv.Key.Source, kv.Key.Target, kv.Key.Relation, kv.Value)).ToArray(), problems, limits);
    }

    private static bool IsLocal(string raw) => raw.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || raw is "127.0.0.1" or "::1" or ".";
}
