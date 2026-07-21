using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Engine-local JSONPath query over a JSON payload. Source is either a file on the engine
/// host (<c>source=file</c>, <c>path=…</c>) or an inline string (<c>source=inline</c>,
/// <c>content=…</c> — typically fed by <c>{{prev.output}}</c>).
///
/// Config:
///   source      "file" | "inline"            (default "inline")
///   path        string                        (when source=file)
///   content     string                        (when source=inline)
///   jsonPath    string, required              (e.g. "$.items[0].name", "$..author",
///                                               "$.items[?(@.price > 10)].name")
///   resultMode  "single" | "all"             (default "single")
///
/// Result:
///   Success → Output = token value (single) or JSON array of matches (all);
///             OutputParameters["result"] = Output,
///             OutputParameters["count"] = match count.
///   Failure → ErrorOutput carries the exception message (invalid JSON, invalid JSONPath,
///             file not found).
///
/// Hardening:
///   - M-7: payload size capped to 8 MiB (file or inline) to prevent OOM on a malicious
///     / runaway upstream. Parse depth capped to 64 to block stack-overflow on deeply
///     nested input.
///   - M-8: file-mode paths go through <see cref="PathGuard"/> so admins can opt into
///     traversal-rejection / allow-listed roots (same config as <c>FileOperationActivity</c>).
/// </summary>
public class JsonQueryActivity : IActivityExecutor
{
    private const int MaxJsonBytes = 8 * 1024 * 1024;
    private const int MaxJsonDepth = 64;

    private readonly IConfiguration? _config;

    public JsonQueryActivity(IConfiguration? config = null)
    {
        _config = config;
    }

    public string ActivityType => "jsonQuery";

    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
        => ActivityExecution.RunAsync(async () =>
        {
            var source = config.GetStringOrNull("source")?.ToLowerInvariant() ?? "inline";
            var jsonPath = config.GetStringOrNull("jsonPath");
            var resultMode = config.GetStringOrNull("resultMode")?.ToLowerInvariant() ?? "single";

            if (string.IsNullOrWhiteSpace(jsonPath))
                return Fail("'jsonPath' is required");

            var loaded = await LoadJsonAsync(source, config, ct);
            if (loaded.Error is not null) return loaded.Error;
            var json = loaded.Json!;

            if (json.Length > MaxJsonBytes)
                return Fail($"input is {json.Length} chars; exceeds limit of {MaxJsonBytes}.");

            var parsed = ParseJson(json);
            if (parsed.Error is not null) return parsed.Error;

            var query = ExecuteQuery(parsed.Root!, jsonPath, resultMode);
            if (query.Error is not null) return query.Error;

            return new ActivityResult
            {
                Success = true,
                Output = query.Output,
                OutputParameters = new Dictionary<string, string>
                {
                    ["result"] = query.Output,
                    ["count"] = query.Count.ToString(),
                },
            };
        }, ex => $"JsonQuery error: {ex.Message}");

    private async Task<(string? Json, ActivityResult? Error)> LoadJsonAsync(string source, JsonElement config, CancellationToken ct)
    {
        if (source == "file")
        {
            var path = config.GetStringOrNull("path");
            if (string.IsNullOrWhiteSpace(path))
                return (null, Fail("'path' is required when source=file"));

            // M-8: apply the same PathGuard config the FileSystemOperation activity uses, so ops
            // can restrict file-mode JsonQuery to allow-listed roots / block traversal.
            if (_config is not null)
            {
                try { PathGuard.Validate(_config, path); }
                catch (InvalidOperationException ex)
                {
                    return (null, Fail($"file access denied: {ex.Message}"));
                }
            }

            if (!File.Exists(path))
                return (null, Fail($"file not found: {path}"));

            // M-7: check size before reading so a 10 GiB file doesn't pin the managed heap.
            var fi = new FileInfo(path);
            if (fi.Length > MaxJsonBytes)
                return (null, Fail($"file '{path}' is {fi.Length} bytes; exceeds limit of {MaxJsonBytes}."));

            return (await File.ReadAllTextAsync(path, ct), null);
        }

        var inline = config.GetString("content", "");
        if (string.IsNullOrWhiteSpace(inline))
            return (null, Fail("'content' is required when source=inline"));
        return (inline, null);
    }

    private static (JToken? Root, ActivityResult? Error) ParseJson(string json)
    {
        try
        {
            // Newtonsoft's JsonLoadSettings exposes MaxDepth; setting it means deeply-nested
            // input fails predictably rather than throwing StackOverflow during traversal.
            var reader = new JsonTextReader(new StringReader(json))
            {
                MaxDepth = MaxJsonDepth,
            };
            return (JToken.ReadFrom(reader), null);
        }
        catch (JsonReaderException ex)
        {
            return (null, Fail($"parse failed: {ex.Message}"));
        }
    }

    private static (string Output, int Count, ActivityResult? Error) ExecuteQuery(JToken root, string jsonPath, string resultMode)
    {
        if (resultMode == "all")
        {
            var matches = root.SelectTokens(jsonPath).ToList();
            return (JsonConvert.SerializeObject(matches), matches.Count, null);
        }

        JToken? token;
        try
        {
            token = root.SelectToken(jsonPath);
        }
        catch (Newtonsoft.Json.JsonException ex) when (ex.Message.Contains("returned multiple tokens", StringComparison.Ordinal))
        {
            return ("", 0, Fail($"path '{jsonPath}' matched multiple tokens but resultMode is 'single'. Set resultMode to 'all' to receive a JSON array of matches."));
        }

        var output = token is null
            ? ""
            : token.Type switch
            {
                JTokenType.String => token.Value<string>() ?? "",
                JTokenType.Null => "",
                JTokenType.Object or JTokenType.Array => token.ToString(Formatting.None),
                _ => token.ToString(),
            };
        return (output, token is null ? 0 : 1, null);
    }

    private static ActivityResult Fail(string message) =>
        new() { Success = false, ErrorOutput = $"JsonQuery: {message}" };
}
