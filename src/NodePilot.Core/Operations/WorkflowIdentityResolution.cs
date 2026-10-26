namespace NodePilot.Core.Operations;

/// <summary>Exact-case names win before the case-insensitive tier.</summary>
public static class WorkflowIdentityResolution
{
    public static WorkflowCallGraphIdentity[] Matches(IEnumerable<WorkflowCallGraphIdentity> identities, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return [];
        var candidates = identities.Where(w => string.Equals(w.Name, trimmed, StringComparison.OrdinalIgnoreCase)).ToArray();
        var exact = candidates.Where(w => string.Equals(w.Name, trimmed, StringComparison.Ordinal)).ToArray();
        return exact.Length > 0 ? exact : candidates;
    }
}
