using System.Text.Json;
using Json.Schema;

namespace NodePilot.Ai.Agents;

public static class AgentJsonSchema
{
    internal static string? ArgumentError(JsonSchema schema, JsonElement input, JsonElement? contract = null)
    {
        var result = schema.Evaluate(input, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        if (result.IsValid) return null;
        static IEnumerable<string> Errors(EvaluationResults node)
        {
            if (node.Details is not null)
                foreach (var child in node.Details)
                    foreach (var error in Errors(child)) yield return error;
            if (node.Errors is not null)
                foreach (var keyword in node.Errors.Keys)
                {
                    var path = node.InstanceLocation.ToString();
                    // Never echo rejected argument values in schema diagnostics.
                    yield return $"{(path.Length > 120 ? path[..120] : path)} ({keyword})";
                }
        }
        var details = contract.HasValue ? Constraints(contract.Value, input, "").Take(6).ToArray() : [];
        return "Tool arguments do not match the tool schema: " + string.Join("; ",
            details.Length > 0 ? details : Errors(result).Distinct().Take(6))
            + ". Correct the named fields and retry only the rejected call; it was not executed.";
    }

    private static IEnumerable<string> Constraints(JsonElement schema, JsonElement value, string path)
    {
        if (schema.ValueKind != JsonValueKind.Object) yield break;
        string Location(string name)
        {
            var location = path + "/" + name.Replace("~", "~0").Replace("/", "~1");
            return location.Length > 160 ? location[..160] : location;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var field in required.EnumerateArray())
                    if (!value.TryGetProperty(field.GetString()!, out _)) yield return Location(field.GetString()!) + " is required";
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var field in properties.EnumerateObject())
                    if (value.TryGetProperty(field.Name, out var child))
                        foreach (var error in Constraints(field.Value, child, Location(field.Name))) yield return error;
        }
        if (value.ValueKind == JsonValueKind.String && schema.TryGetProperty("maxLength", out var length)
            && value.GetString()!.EnumerateRunes().Count() > length.GetInt32())
            yield return $"{path} maxLength={length.GetInt32()}; shorten the text";
        if (value.ValueKind == JsonValueKind.String && schema.TryGetProperty("minLength", out var minimum)
            && value.GetString()!.EnumerateRunes().Count() < minimum.GetInt32())
            yield return $"{path} minLength={minimum.GetInt32()}; provide a nonempty value";
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("maxItems", out var items) && value.GetArrayLength() > items.GetInt32())
                yield return $"{path} maxItems={items.GetInt32()}; keep only the decisive entries";
            if (schema.TryGetProperty("items", out var itemSchema))
                for (var index = 0; index < value.GetArrayLength(); index++)
                    foreach (var error in Constraints(itemSchema, value[index], Location(index.ToString()))) yield return error;
        }
    }

    public static JsonSchema Compile(JsonElement schema)
    {
        RejectExternalReferences(schema);
        // Agent schemas cannot fetch documents from the network or local filesystem.
        var registry = new SchemaRegistry { Fetch = (_, _) => null };
        try { return JsonSchema.Build(schema, new BuildOptions { SchemaRegistry = registry }); }
        catch (JsonSchemaException ex) { throw new ArgumentException("Invalid agent JSON Schema: " + ex.Message, nameof(schema), ex); }
    }

    private static void RejectExternalReferences(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "$ref" or "$dynamicRef" && property.Value.ValueKind == JsonValueKind.String
                    && !(property.Value.GetString() ?? "").StartsWith('#'))
                    throw new ArgumentException("Agent schemas support only local fragment references.");
                RejectExternalReferences(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectExternalReferences(item);
    }

    public static bool IsValid(JsonSchema schema, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid;
        }
        catch (JsonException) { return false; }
    }
}
