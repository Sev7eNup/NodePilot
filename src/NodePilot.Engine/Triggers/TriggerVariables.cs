namespace NodePilot.Engine.Triggers;

/// <summary>
/// Shared helpers for the trigger node-executors. When a background source fires a workflow,
/// the trigger payload arrives in <c>context.Variables</c> under the <c>manual.</c> prefix
/// (there is no <c>trigger.*</c> namespace); every trigger node surfaces the same flat,
/// prefix-stripped view of it as its OutputParameters.
/// </summary>
internal static class TriggerVariables
{
    /// <summary>
    /// Copies every <c>manual.*</c> entry into a flat dictionary with the prefix stripped.
    /// Returns an empty dictionary when the node runs manually without trigger payload.
    /// </summary>
    internal static Dictionary<string, string> ExtractManualParams(IReadOnlyDictionary<string, string> variables)
    {
        var result = new Dictionary<string, string>();
        foreach (var (k, v) in variables)
            if (k.StartsWith("manual.", StringComparison.OrdinalIgnoreCase))
                result[k["manual.".Length..]] = v;
        return result;
    }
}
