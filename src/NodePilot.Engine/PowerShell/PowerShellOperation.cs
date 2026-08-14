using System.Text.Json;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.PowerShell;

/// <summary>
/// Shared helpers for structured PowerShell-backed Activities. Run Script intentionally does
/// not use this module because it has user-script, parameter injection, and transcript semantics.
/// </summary>
internal static class PowerShellOperation
{
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static PowerShellOperationMarkers Markers(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("PowerShell operation marker token is required.", nameof(token));

        var normalized = token.Trim().ToUpperInvariant();
        return new PowerShellOperationMarkers(
            $"###NODEPILOT_{normalized}_RESULT_START###",
            $"###NODEPILOT_{normalized}_RESULT_END###");
    }

    public static string Literal(string? value) => PowerShellQuoter.Literal(value);

    public static bool TryExtractJsonBlock(
        string? output,
        PowerShellOperationMarkers markers,
        out PowerShellOperationJsonBlock block)
    {
        block = default;
        if (string.IsNullOrEmpty(output)) return false;

        var startIdx = output.IndexOf(markers.Start, StringComparison.Ordinal);
        var endIdx = output.IndexOf(markers.End, StringComparison.Ordinal);
        if (startIdx < 0 || endIdx <= startIdx) return false;

        var jsonStart = startIdx + markers.Start.Length;
        block = new PowerShellOperationJsonBlock(
            output.Substring(jsonStart, endIdx - jsonStart).Trim(),
            output.Substring(0, startIdx).TrimEnd());
        return true;
    }

    public static bool TryParseJsonBlock(
        string? output,
        PowerShellOperationMarkers markers,
        out JsonDocument? document,
        out string? parseError)
    {
        document = null;
        parseError = null;

        if (!TryExtractJsonBlock(output, markers, out var block))
            return false;

        return TryParseJson(block.Json, out document, out parseError);
    }

    public static bool TryParseJson(string json, out JsonDocument? document, out string? parseError)
    {
        document = null;
        parseError = null;

        try
        {
            document = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException ex)
        {
            parseError = ex.Message;
            return false;
        }
    }

    public static bool TryDeserializeJson<T>(string json, out T? value, out string? parseError)
    {
        value = default;
        parseError = null;

        try
        {
            value = JsonSerializer.Deserialize<T>(json, CaseInsensitiveJson);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            parseError = ex.Message;
            return false;
        }
    }

    public static int? TimeoutSecondsFromConfig(JsonElement config)
        => config.TryGetProperty("timeoutSeconds", out var p)
           && p.TryGetInt32(out var seconds)
           && seconds > 0
            ? seconds
            : null;

    public static TimeSpan? ToTimeSpan(int? timeoutSeconds)
        => timeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

    public static int ToWaitForExitMilliseconds(int? timeoutSeconds)
    {
        if (timeoutSeconds is not { } seconds) return -1;
        var milliseconds = TimeSpan.FromSeconds(seconds).TotalMilliseconds;
        return milliseconds >= int.MaxValue ? int.MaxValue : (int)milliseconds;
    }

    public static void CopyStringField(JsonElement obj, string sourceKey, IDictionary<string, string> dest, string destKey)
    {
        if (!obj.TryGetProperty(sourceKey, out var value)) return;
        dest[destKey] = JsonElementToScalarString(value);
    }

    public static Dictionary<string, string> MapObjectFields(
        JsonElement obj,
        params (string SourceKey, string DestKey)[] fields)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sourceKey, destKey) in fields)
            CopyStringField(obj, sourceKey, result, destKey);
        return result;
    }

    /// <summary>
    /// Projects a bare <c>ConvertTo-Json</c> status object (no marker envelope) into the
    /// requested fields. ConvertTo-Json wraps a single result in <c>{}</c> but multiple results
    /// in <c>[]</c>, so a 1-element array is defensively unwrapped. Non-JSON output — e.g. the
    /// cmdlet printed an error instead — yields an empty dictionary rather than throwing.
    /// </summary>
    public static Dictionary<string, string> MapStatusJsonFields(
        string? json,
        params (string SourceKey, string DestKey)[] fields)
    {
        if (json is null || !TryParseJson(json, out var parsedDoc, out _))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var doc = parsedDoc!;
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1)
            root = root[0];

        return root.ValueKind == JsonValueKind.Object
            ? MapObjectFields(root, fields)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public static string JsonElementToScalarString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.GetRawText(),
    };
}

internal readonly record struct PowerShellOperationMarkers(string Start, string End)
{
    public string RenderJsonEnvelope(string resultExpression, int depth = 6)
    {
        if (string.IsNullOrWhiteSpace(resultExpression))
            throw new ArgumentException("PowerShell result expression is required.", nameof(resultExpression));

        return $$"""
            Write-Output '{{Start}}'
            Write-Output ({{resultExpression}} | ConvertTo-Json -Depth {{depth}} -Compress)
            Write-Output '{{End}}'
            """;
    }
}

internal readonly record struct PowerShellOperationJsonBlock(string Json, string LeadingOutput);
