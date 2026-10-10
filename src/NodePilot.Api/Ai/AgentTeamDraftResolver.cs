using System.Text.Json;
using System.Text.RegularExpressions;
using NodePilot.Ai;
using NodePilot.Core.Agents;

namespace NodePilot.Api.Ai;

/// <summary>
/// Turns a model draft (names) into a patch (ids), deterministically. Every reference must be
/// an exact inventory match <em>and</em> appear in the user's request; paths and hosts must be
/// copied from the request. Unresolved machine, credential and service-identity requests are
/// <c>blocking</c> issues so a member never silently inherits a different target. Everything
/// else that cannot be honoured is dropped with a warning. The merged result is validated with
/// <see cref="AgentConfiguration.Validate"/>.
/// </summary>
public static partial class AgentTeamDraftResolver
{
    private const int MaxCandidates = 50;
    private static readonly StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    public static GenerateAgentTeamResponse Resolve(AgentTeamDraftResult result, string prompt,
        AgentTeamResolvableInventory inventory, JsonElement? currentConfig)
    {
        var issues = new List<AgentTeamIssue>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hostTokens = HostTokens(prompt);
        var members = new List<AgentDefinition>();

        foreach (var m in result.Draft.Members)
        {
            var id = UniqueId(m.Id, ids);
            var resolver = new MemberResolver(id, prompt, inventory, hostTokens, issues);
            var machineId = resolver.Machine(m.Machine);
            var identity = resolver.ServiceIdentity(m.ServiceIdentity);
            var credentialId = identity ? null : resolver.Credential(m.Credential);
            members.Add(new AgentDefinition
            {
                Id = id,
                Role = Truncate(m.Role, 128),
                Instructions = m.Instructions,
                IsSupervisor = m.IsSupervisor,
                IsReviewer = m.IsReviewer && !m.IsSupervisor,
                TargetMachineId = machineId,
                CredentialId = credentialId,
                UseServiceIdentity = identity,
                SkillIds = resolver.Skills(m.Skills),
                Tools = resolver.Tools(m.Tools)
            });
        }

        var patch = Merge(members, result.Draft, prompt, currentConfig, issues);
        return new GenerateAgentTeamResponse(patch, ReferencedNames(patch.Members, inventory), issues, result.Retried,
            result.DurationMs, result.Model, result.PromptTokens, result.CompletionTokens, result.TotalTokens);
    }

    private static Dictionary<Guid, string> ReferencedNames(IEnumerable<AgentDefinition> members, AgentTeamResolvableInventory inv)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var m in members)
        {
            if (m.TargetMachineId is { } machine && inv.Machines.FirstOrDefault(x => x.Id == machine) is { } mi)
                names[machine] = mi.Name;
            if (m.CredentialId is { } cred && inv.Credentials.FirstOrDefault(x => x.Id == cred) is { } ci)
                names[cred] = ci.Name;
            foreach (var id in m.SkillIds)
                if (inv.Skills.FirstOrDefault(x => x.Id == id) is { } skill) names[id] = $"{skill.Name} {skill.Version}";
            foreach (var tool in m.Tools)
            {
                foreach (var id in tool.WorkflowIds)
                    if (inv.Workflows.FirstOrDefault(x => x.Id == id) is { } wf) names[id] = wf.Name;
                if (tool.McpServerId is { } server && inv.McpServers.FirstOrDefault(x => x.Id == server) is { } mcp)
                    names[server] = mcp.Name;
            }
        }
        return names;
    }

    private static AgentTeamPatch Merge(List<AgentDefinition> members, AgentTeamDraft draft, string prompt,
        JsonElement? currentConfig, List<AgentTeamIssue> issues)
    {
        var current = ReadCurrent(currentConfig);
        var task = !string.IsNullOrWhiteSpace(current.Task) ? current.Task
            : Truncate(!string.IsNullOrWhiteSpace(draft.Task) ? draft.Task! : prompt.Trim(), 64_000);
        var parallel = draft.MaxParallelMembers ?? current.MaxParallelMembers;
        if (parallel.HasValue) parallel = Math.Clamp(parallel.Value, 1, Math.Max(1, members.Count - 1));

        var patch = new AgentTeamPatch(members.ToArray(), task, parallel);
        try
        {
            // Same merge the editor applies: members, task and parallel limit replace the
            // node's values, everything else (budgets, result format) stays as configured.
            AgentConfiguration.Validate(current with { Members = patch.Members, Task = task, MaxParallelMembers = parallel }, team: true);
        }
        catch (ArgumentException ex)
        {
            issues.Add(new AgentTeamIssue("blocking", "", "team", "invalid_config", ex.Message, null, []));
        }
        return patch;
    }

    private static AgentActivityConfiguration ReadCurrent(JsonElement? config)
    {
        if (config is not { ValueKind: JsonValueKind.Object } el) return new AgentActivityConfiguration();
        try
        {
            var value = el.Deserialize<AgentActivityConfiguration>(AgentConfiguration.JsonOptions) ?? new();
            // The shared timeout field stores zero for "use the configured default".
            return value.TimeoutSeconds == 0 ? value with { TimeoutSeconds = null } : value;
        }
        catch (JsonException)
        {
            return new AgentActivityConfiguration();
        }
    }

    internal static string UniqueId(string raw, HashSet<string> used)
    {
        var id = IdInvalidChars().Replace(raw ?? "", "-").Trim('-');
        if (id.Length == 0) id = "member";
        if (id.Length > 60) id = id[..60];
        var candidate = id;
        for (var n = 2; !used.Add(candidate); n++) candidate = $"{id}-{n}";
        return candidate;
    }

    internal static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    internal static IReadOnlyList<string> HostTokens(string prompt)
        => HostPattern().Matches(prompt).Select(m => m.Value.TrimEnd('.').ToLowerInvariant())
            .Where(h => h.Length > 0).Distinct().ToList();

    internal static string NormalizePath(string path)
        => path.Trim().TrimEnd('.', ',', ';', ')', ']', '"', '\'').Replace('/', '\\').TrimEnd('\\');

    /// <summary>
    /// A path is accepted when it is absolute and it, or one of its leading directories, is a path
    /// the user wrote — that is, it appears in the request ending at a path boundary. A parent
    /// of a written path (including a drive root the user did not write), relative paths and
    /// <c>..</c> segments are rejected. Spaces in paths are fine because the match is verbatim.
    /// </summary>
    internal static bool PathAllowed(string path, string prompt)
    {
        var p = NormalizePath(path);
        if (!AbsolutePath().IsMatch(p) || p.Split('\\').Any(s => s == "..")) return false;
        var text = prompt.Replace('/', '\\');
        // p itself, then each leading directory (cut at a separator).
        var end = p.Length;
        while (end >= 2)
        {
            if (WrittenInRequest(text, p[..end])) return true;
            end = end > 1 ? p.LastIndexOf('\\', end - 1) : -1;
        }
        return false;
    }

    private static bool WrittenInRequest(string text, string prefix)
    {
        for (var at = text.IndexOf(prefix, Ci); at >= 0; at = text.IndexOf(prefix, at + 1, Ci))
        {
            var before = at == 0 ? ' ' : text[at - 1];
            if (char.IsLetterOrDigit(before) || before == '\\') continue;
            var end = at + prefix.Length;
            if (end >= text.Length || !(char.IsLetterOrDigit(text[end]) || "\\_-".Contains(text[end]))) return true;
            // "C:\" written with a trailing separator and nothing after it.
            if (text[end] == '\\' && (end + 1 >= text.Length || char.IsWhiteSpace(text[end + 1]) || "\"'".Contains(text[end + 1]))) return true;
        }
        return false;
    }

    private sealed class MemberResolver(string memberId, string prompt, AgentTeamResolvableInventory inv,
        IReadOnlyList<string> hostTokens, List<AgentTeamIssue> issues)
    {
        public Guid? Machine(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            if (!Mentioned(reference))
                return Ignored("machine", reference, "The request does not name this machine; the member keeps the step's target.");
            var matches = inv.Machines.Where(m => m.Name.Equals(reference, Ci) || m.Hostname.Equals(reference, Ci)).ToList();
            if (matches.Count == 1) return matches[0].Id;
            var candidates = (matches.Count > 1 ? matches.AsEnumerable() : inv.Machines.OrderByDescending(m => Near(reference, m.Name, m.Hostname)))
                .Take(MaxCandidates).Select(m => new AgentTeamCandidate(m.Id, m.Name, m.Hostname)).ToList();
            Blocking("machine", matches.Count > 1 ? "ambiguous" : "unresolved", reference,
                matches.Count > 1 ? $"Machine '{reference}' matches more than one machine." : $"Machine '{reference}' was not found.", candidates);
            return null;
        }

        public Guid? Credential(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            if (!Mentioned(reference))
                return Ignored("credential", reference, "The request does not name this credential; the member keeps the inherited credential.");
            var matches = inv.Credentials.Where(c => c.Name.Equals(reference, Ci)).ToList();
            if (matches.Count == 1) return matches[0].Id;
            var candidates = (matches.Count > 1 ? matches.AsEnumerable() : inv.Credentials.OrderByDescending(c => Near(reference, c.Name)))
                .Take(MaxCandidates).Select(c => new AgentTeamCandidate(c.Id, c.Name, null)).ToList();
            Blocking("credential", matches.Count > 1 ? "ambiguous" : "unresolved", reference,
                matches.Count > 1 ? $"Credential '{reference}' matches more than one credential." : $"Credential '{reference}' was not found.", candidates);
            return null;
        }

        public bool ServiceIdentity(bool requested)
        {
            if (!requested) return false;
            if (!ServiceIdentityPhrase().IsMatch(prompt))
            {
                Warn("serviceIdentity", "not_in_request", null, "The request does not ask for the service identity; it was not enabled.");
                return false;
            }
            if (inv.ServiceIdentityAvailable) return true;
            Blocking("serviceIdentity", "not_available", null,
                "The service identity was requested but is disabled or needs an administrator.", []);
            return false;
        }

        public Guid[] Skills(IReadOnlyList<AgentTeamDraftSkill> skills)
        {
            var result = new List<Guid>();
            foreach (var s in skills)
            {
                if (!Mentioned(s.Name)) { Warn("skill", "not_in_request", s.Name, $"Skill '{s.Name}' is not named in the request; skipped."); continue; }
                var matches = inv.Skills.Where(x => x.Name.Equals(s.Name, Ci)
                    && (string.IsNullOrWhiteSpace(s.Version) || x.Version.Equals(s.Version, Ci))).ToList();
                if (matches.Count == 1) { if (!result.Contains(matches[0].Id)) result.Add(matches[0].Id); }
                else Warn("skill", matches.Count > 1 ? "ambiguous" : "unresolved", s.Name,
                    matches.Count > 1 ? $"Skill '{s.Name}' exists in several versions; name a version." : $"Skill '{s.Name}' was not found or is disabled.");
            }
            return result.Take(20).ToArray();
        }

        public AgentToolSelection[] Tools(IReadOnlyList<AgentTeamDraftTool> tools)
        {
            var result = new List<AgentToolSelection>();
            foreach (var t in tools)
            {
                if (t.Name == "files_write") { Warn("tool", "files_write_removed", t.Name, "File writing is blocked by the read-only policy; the tool was removed."); continue; }
                if (t.Name != "mcp" && (!AgentConfiguration.NativeTools.Contains(t.Name) || result.Any(r => r.Name == t.Name)))
                { Warn("tool", "unknown_tool", t.Name, $"Tool '{t.Name}' is unknown or duplicated; skipped."); continue; }
                var selection = Tool(t);
                if (selection is not null) result.Add(selection);
            }
            return result.Take(64).ToArray();
        }

        private AgentToolSelection? Tool(AgentTeamDraftTool t)
        {
            switch (t.Name)
            {
                case "files_list" or "files_read" or "files_search" or "logs_collect":
                    var paths = t.Paths.Where(p => { var ok = PathAllowed(p, prompt); if (!ok) Warn("tool", "path_rejected", p, $"Path '{p}' is not in the request (or is wider than it); removed from {t.Name}."); return ok; })
                        .Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (paths.Length > 0) return new AgentToolSelection { Name = t.Name, AllowedPaths = paths };
                    Warn("tool", "tool_dropped", t.Name, $"{t.Name} needs a path from the request; the tool was removed.");
                    return null;
                case "http_request":
                    var hosts = t.Hosts.Where(h => { var ok = hostTokens.Contains(h.Trim().TrimEnd('.').ToLowerInvariant()); if (!ok) Warn("tool", "host_rejected", h, $"Host '{h}' is not in the request; removed from http_request."); return ok; })
                        .Select(h => h.Trim().TrimEnd('.')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (t.Hosts.Count > 0 && hosts.Length == 0)
                    {
                        // An empty host list means "any host", so a rejected list must drop the tool instead.
                        Warn("tool", "tool_dropped", t.Name, "None of the proposed hosts is in the request; http_request was removed.");
                        return null;
                    }
                    if (hosts.Length == 0)
                        Warn("tool", "http_unrestricted", t.Name, "http_request has no host restriction: the agent may read any host (GET/HEAD, network policy applies).");
                    return new AgentToolSelection { Name = t.Name, AllowedHosts = hosts };
                case "workflow_run":
                    var workflowIds = new List<Guid>();
                    foreach (var name in t.Workflows)
                    {
                        if (!Mentioned(name)) { Warn("tool", "not_in_request", name, $"Workflow '{name}' is not named in the request; skipped."); continue; }
                        var matches = inv.Workflows.Where(w => w.Name.Equals(name, Ci)).ToList();
                        if (matches.Count == 1) workflowIds.Add(matches[0].Id);
                        else Warn("tool", matches.Count > 1 ? "ambiguous" : "unresolved", name,
                            matches.Count > 1 ? $"Workflow '{name}' is ambiguous." : $"Workflow '{name}' is not published, enabled and runnable by you.");
                    }
                    if (workflowIds.Count > 0) return new AgentToolSelection { Name = t.Name, WorkflowIds = workflowIds.Distinct().ToArray() };
                    Warn("tool", "tool_dropped", t.Name, "workflow_run has no usable workflow; the tool was removed.");
                    return null;
                case "mcp":
                    var server = inv.McpServers.FirstOrDefault(s => s.Name.Equals(t.McpServer, Ci));
                    var tool = server?.Tools.FirstOrDefault(x => x.Equals(t.McpTool, StringComparison.Ordinal));
                    if (server is not null && tool is not null && Mentioned(server.Name))
                        return new AgentToolSelection { Name = "mcp", McpServerId = server.Id, McpToolName = tool };
                    Warn("tool", "no_valid_grant", $"{t.McpServer}/{t.McpTool}",
                        "No current read approval for this MCP server and tool, or the server is not named in the request; removed. The tool contract is checked again when the agent runs.");
                    return null;
                default:
                    return new AgentToolSelection { Name = t.Name };
            }
        }

        private bool Mentioned(string value) => prompt.Contains(value, Ci);

        private Guid? Ignored(string field, string reference, string message)
        {
            Warn(field, "not_in_request", reference, message);
            return null;
        }

        private static int Near(string reference, params string[] names)
            => names.Any(n => n.Contains(reference, Ci) || reference.Contains(n, Ci)) ? 1 : 0;

        private void Warn(string field, string code, string? reference, string message)
            => issues.Add(new AgentTeamIssue("warning", memberId, field, code, message, reference, []));

        private void Blocking(string field, string code, string? reference, string message, IReadOnlyList<AgentTeamCandidate> candidates)
            => issues.Add(new AgentTeamIssue("blocking", memberId, field, code, message, reference, candidates));
    }

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex IdInvalidChars();

    [GeneratedRegex(@"^(?:[A-Za-z]:(?:\\|$)|\\)", RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePath();

    [GeneratedRegex(@"[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"(service|dienst)[\s-]*(identit|konto|account)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServiceIdentityPhrase();
}
