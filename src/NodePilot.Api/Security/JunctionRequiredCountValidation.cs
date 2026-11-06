using System.Text.Json;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Api.Security;

/// <summary>
/// Publish/enable validation for waitNofM junctions.
///
/// <para>A draft may wait for more inputs than it has while it is being wired, so save stays
/// permissive. A live junction like that can never be satisfied: the scheduler skips it and
/// everything after it on every run, without a failed step.</para>
/// </summary>
internal static class JunctionRequiredCountValidation
{
    internal static string? ValidateDefinition(string definitionJson)
    {
        if (!WorkflowDefinitionDocument.TryParse(definitionJson, out var definition)
            || definition is null)
        {
            return null; // Structural validation owns malformed definitions.
        }

        foreach (var junction in definition.Nodes.Where(x =>
                     string.Equals(x.Type, "junction", StringComparison.Ordinal) && !x.Data.Disabled))
        {
            var config = junction.Data.Config;
            if (config.ValueKind != JsonValueKind.Object
                || !config.TryGetProperty("mode", out var mode)
                || mode.ValueKind != JsonValueKind.String
                || !string.Equals(mode.GetString(), "waitNofM", StringComparison.Ordinal)
                || !config.TryGetProperty("requiredCount", out var countElement)
                || countElement.ValueKind != JsonValueKind.Number
                || !countElement.TryGetInt32(out var required))
            {
                continue;
            }

            // The same active inputs the scheduler counts: disabled edges and nodes drop out.
            var inputs = definition.IncomingEdgesByTarget.TryGetValue(junction.Id, out var edges) ? edges.Count : 0;
            if (required > inputs)
            {
                return $"junction '{junction.Id}' waits for {required} inputs but has only {inputs} enabled "
                    + "incoming edge(s). Lower requiredCount or connect more inputs.";
            }
        }

        return null;
    }
}
