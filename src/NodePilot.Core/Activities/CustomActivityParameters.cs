using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodePilot.Core.Activities;

/// <summary>
/// Input field descriptor for a custom activity. Stored as a JSON array in
/// <c>CustomActivityDefinition.InputParametersJson</c>, parsed here by the executor to inject
/// values and by the controller to validate, and sent to the frontend as the config-form schema.
/// </summary>
public sealed record CustomActivityInputParameter(
    string Name,
    string Label,
    string Type,
    bool Required = false,
    string? Default = null,
    IReadOnlyList<string>? Options = null,
    string? Description = null);

/// <summary>Output descriptor. The set of <see cref="Name"/>s is the wrapper's capture
/// allow-list.</summary>
public sealed record CustomActivityOutputParameter(string Name, string Type);

/// <summary>
/// Allowed <c>type</c> values for input and output fields. There is no <c>secret</c> type:
/// redaction is key-name based, so a free-form secret field cannot be redacted out of workflow
/// JSON, export, backup or AI context. Secrets have to arrive via <c>{{globals.X}}</c> or
/// credentials.
/// </summary>
public static class CustomActivityParameterTypes
{
    public static readonly IReadOnlySet<string> Input =
        new HashSet<string>(StringComparer.Ordinal) { "string", "number", "boolean", "select", "multiline" };

    public static readonly IReadOnlySet<string> Output =
        new HashSet<string>(StringComparer.Ordinal) { "string", "number", "boolean", "object", "array" };
}

/// <summary>JSON (de)serialization for the parameter arrays. Malformed JSON gives an empty
/// list.</summary>
public static class CustomActivityParameters
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IReadOnlyList<CustomActivityInputParameter> ParseInputs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<CustomActivityInputParameter>>(json, Options) ?? []; }
        catch (JsonException) { return []; }
    }

    public static IReadOnlyList<CustomActivityOutputParameter> ParseOutputs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<CustomActivityOutputParameter>>(json, Options) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string Serialize<T>(IReadOnlyList<T> items) => JsonSerializer.Serialize(items, Options);
}
