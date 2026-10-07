using System.Text.Json;

namespace NodePilot.Ai.Agents;

internal sealed record DelegationResponse(string Status, string Content, bool RequiresNewEvidence = true, string[]? ReviewDependencies = null)
{
    public static string Contract(bool reviewer) => reviewer
        ? "Return one JSON object with top-level status (completed, needs_input, or failed), content (string), verdict (approved or needs_work), and openChecks (string array). For an objection, set objectionKind to evidence when new observations are needed, or revision when existing evidence suffices and only interpretation, wording or the proposed next step needs correction. Omitted objectionKind defaults to evidence. Do not nest these fields inside content or return the supervisor's final schema. Use approved and empty openChecks only when the requested review outcome is supported."
        : "Return one JSON object with top-level status (completed, needs_input, or failed) and content (string). Do not return the supervisor's final schema.";

    public static DelegationResponse Parse(string text, bool reviewer, IReadOnlyCollection<string>? reviewSources = null)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String
            || status.GetString() is not ("completed" or "needs_input" or "failed")
            || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw new JsonException("Invalid delegation envelope. " + Contract(reviewer));
        var requiresNewEvidence = true;
        if (reviewer && root.TryGetProperty("objectionKind", out var kind))
        {
            if (kind.ValueKind != JsonValueKind.String || kind.GetString() is not ("evidence" or "revision"))
                throw new JsonException("objectionKind must be evidence or revision.");
            requiresNewEvidence = kind.GetString() == "evidence";
        }
        string[]? dependencies = null;
        if (reviewer && root.TryGetProperty("reviewDependencies", out var basis))
        {
            if (basis.ValueKind != JsonValueKind.Array || basis.GetArrayLength() == 0 || basis.GetArrayLength() > 100
                || basis.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())))
                throw new JsonException("reviewDependencies must be a nonempty array of source keys; omit it for a whole-team review.");
            dependencies = basis.EnumerateArray().Select(v => v.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
            if (reviewSources is not null && dependencies.Any(source => !reviewSources.Contains(source)))
                throw new JsonException("reviewDependencies contains an unknown source. Use only supplied reviewSources keys, or omit reviewDependencies for a whole-team review.");
        }
        var result = new DelegationResponse(status.GetString()!, content.GetString()!, requiresNewEvidence, dependencies);
        if (!reviewer) return result;
        var hasChecks = root.TryGetProperty("openChecks", out var checks);
        if (hasChecks && (checks.ValueKind != JsonValueKind.Array
            || checks.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String)))
            throw new JsonException("openChecks must be a string array.");
        if (result.Status != "completed")
            return hasChecks && checks.GetArrayLength() > 0
                ? result with { Content = "Open checks: " + checks.GetRawText() + "\n" + result.Content }
                : result;
        if (!root.TryGetProperty("verdict", out var verdict) || verdict.ValueKind != JsonValueKind.String
            || verdict.GetString() is not ("approved" or "needs_work")
            || !hasChecks)
            throw new JsonException("Invalid reviewer completion. " + Contract(true));
        return verdict.GetString() == "approved" && checks.GetArrayLength() == 0 ? result
            : result with { Status = "needs_input", Content = "Review requires further work. Open checks: " + checks.GetRawText() + "\n" + result.Content };
    }
}
