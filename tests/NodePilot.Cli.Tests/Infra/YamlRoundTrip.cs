using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace NodePilot.Cli.Tests.Infra;

/// <summary>
/// Reads YAML back into JSON text and types plain scalars like a YAML 1.1 parser (PyYAML):
/// null and boolean words, numbers, and anything else 1.1 could read as a non-string. Comparing
/// the result with the source JSON shows whether emitted YAML kept every value and its type.
/// </summary>
internal static partial class YamlRoundTrip
{
    public static string ToJson(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count != 1)
            throw new InvalidOperationException($"Expected one YAML document, got {stream.Documents.Count}.");
        return Convert(stream.Documents[0].RootNode)?.ToJsonString() ?? "null";
    }

    public static string Normalize(string json) => JsonNode.Parse(json)?.ToJsonString() ?? "null";

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(kv =>
            KeyValuePair.Create(Key((YamlScalarNode)kv.Key), Convert(kv.Value)))),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(Convert).ToArray()),
        YamlScalarNode scalar => Scalar(scalar),
        _ => throw new InvalidOperationException($"Unexpected YAML node {node.NodeType}."),
    };

    private static string Key(YamlScalarNode key)
    {
        var value = key.Value ?? "";
        return key.Style == ScalarStyle.Plain && (value.Length == 0 || NonString().IsMatch(value))
            ? $"<non-string key {value}>"
            : value;
    }

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        if (scalar.Style != ScalarStyle.Plain) return JsonValue.Create(value);
        if (value is "" or "~" or "null" or "Null" or "NULL") return null;
        if (TrueWord().IsMatch(value)) return JsonValue.Create(true);
        if (FalseWord().IsMatch(value)) return JsonValue.Create(false);
        if (JsonNumber().IsMatch(value)) return JsonNode.Parse(value);
        return NonString().IsMatch(value) ? JsonValue.Create($"<non-string {value}>") : JsonValue.Create(value);
    }

    [GeneratedRegex("^(?:y|Y|yes|Yes|YES|true|True|TRUE|on|On|ON)$")]
    private static partial Regex TrueWord();

    [GeneratedRegex("^(?:n|N|no|No|NO|false|False|FALSE|off|Off|OFF)$")]
    private static partial Regex FalseWord();

    [GeneratedRegex(@"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?$")]
    private static partial Regex JsonNumber();

    // Deliberately broad: any plain scalar starting like a number, date or special float, plus
    // the merge key, the value key and every null/boolean word.
    [GeneratedRegex(@"^(?:[-+.]?[0-9].*|[-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN)|<<|=|~|null|Null|NULL|y|Y|yes|Yes|YES|n|N|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF)$")]
    private static partial Regex NonString();
}
