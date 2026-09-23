using System.Text.Json;

namespace NodePilot.Ai.Agents;

internal sealed record DelegationResponse(string Status, string Content, bool RequiresNewEvidence = true)
{
    public static string Contract(bool reviewer) => reviewer
        ? "Return one JSON object with top-level status (completed, needs_input, or failed), content (string), verdict (approved or needs_work), and openChecks (string array). For an objection, set objectionKind to evidence when new observations are needed, or revision when existing evidence suffices and only interpretation, wording or the proposed next step needs correction. Omitted objectionKind defaults to evidence. Do not nest these fields inside content or return the supervisor's final schema. Use approved and empty openChecks only when the requested review outcome is supported."
        : "Return one JSON object with top-level status (completed, needs_input, or failed) and content (string). Do not return the supervisor's final schema.";

    public static DelegationResponse Parse(string text, bool reviewer)
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
        var result = new DelegationResponse(status.GetString()!, content.GetString()!, requiresNewEvidence);
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
            : new("needs_input", "Review requires further work. Open checks: " + checks.GetRawText() + "\n" + result.Content, requiresNewEvidence);
    }
}
