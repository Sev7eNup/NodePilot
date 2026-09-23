using System.Text;
using System.Text.Json;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Agents;

internal static class AgentContentRedactor
{
    internal static string Redact(string text, OutputRedactor redactor) => RedactText(text, redactor, 0);

    private static string RedactText(string text, OutputRedactor redactor, int depth)
    {
        if (depth >= 32) return OutputRedactor.Placeholder;
        var trimmed = text.AsSpan().TrimStart();
        if (!trimmed.IsEmpty && trimmed[0] is '{' or '[' or '"')
        {
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                    Write(document.RootElement, writer, redactor, depth);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            catch (JsonException) { /* Unstructured tool text still uses the standard redactor. */ }
        }
        return redactor.Redact(text) ?? "";
    }

    private static void Write(JsonElement value, Utf8JsonWriter writer, OutputRedactor redactor, int depth)
    {
        if (depth >= 32) { writer.WriteStringValue(OutputRedactor.Placeholder); return; }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (OutputRedactor.IsSensitiveName(property.Name)) writer.WriteStringValue(OutputRedactor.Placeholder);
                    else Write(property.Value, writer, redactor, depth + 1);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(item, writer, redactor, depth + 1);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                // Tool envelopes can contain another JSON document as an escaped string.
                writer.WriteStringValue(RedactText(value.GetString()!, redactor, depth + 1));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
