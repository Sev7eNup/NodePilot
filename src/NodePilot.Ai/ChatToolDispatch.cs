using System.Text.Json;

namespace NodePilot.Ai;

/// <summary>One tool implementation: raw arguments + the registry's per-request context.</summary>
internal delegate Task<object> ChatToolHandler<TContext>(JsonElement args, TContext context, CancellationToken ct);

/// <summary>
/// The dispatch plumbing both chat tool registries share (<see cref="WorkflowChatToolRegistry"/>
/// and <see cref="Knowledge.KnowledgeChatToolRegistry"/>): the <c>{ "error": … }</c> envelope, the
/// tolerant argument parse — a blank or malformed argument blob is a model artefact and must never
/// abort the tool loop — and the serialized result envelope. Tool sets, gating and serializer
/// options stay with the registries.
/// </summary>
internal static class ChatToolDispatch
{
    /// <summary>The error envelope a tool failure comes back as instead of aborting the loop.</summary>
    public static string Error(string message, JsonSerializerOptions json) =>
        JsonSerializer.Serialize(new { error = message }, json);

    /// <summary>Answer for a tool name no registry entry matches (the model can hallucinate names).</summary>
    public static string UnknownTool(string name, JsonSerializerOptions json) =>
        Error($"Unbekanntes Tool: {name}", json);

    /// <summary>Parses a tool's JSON-schema literal into a detached element.</summary>
    public static JsonElement ParseParams(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Runs one tool: tolerant argument parse (blank → <c>{}</c>, malformed → <paramref name="emptyArgs"/>),
    /// handler, serialized result — optionally reshaped by <paramref name="shape"/> (token-budget
    /// truncation). Cancellation propagates; every other exception becomes an error envelope.
    /// </summary>
    public static async Task<string> ExecuteAsync<TContext>(
        ChatToolHandler<TContext> handler,
        string argumentsJson,
        JsonElement emptyArgs,
        TContext context,
        JsonSerializerOptions json,
        CancellationToken ct,
        Func<string, string>? shape = null)
    {
        try
        {
            JsonElement args;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                args = doc.RootElement.Clone();
            }
            catch (JsonException) { args = emptyArgs; }

            var result = await handler(args, context, ct);
            var serialized = JsonSerializer.Serialize(result, json);
            return shape is null ? serialized : shape(serialized);
        }
        catch (OperationCanceledException)
        {
            throw; // Cancellation belongs to the caller's loop, not the error-JSON path.
        }
        catch (Exception ex)
        {
            return Error(ex.Message, json);
        }
    }
}
