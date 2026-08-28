using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Engine-local JSONPath query over a JSON payload, read from a file on the engine host
/// (<c>source=file</c>) or an inline string (<c>source=inline</c>, e.g. <c>{{prev.output}}</c>).
/// <c>resultMode</c> "single" returns the first match, "all" returns a JSON array; both also
/// land in <c>OutputParameters["result"]</c>/<c>["count"]</c>. Payload size is capped at 8 MiB
/// and parse depth at 64 against untrusted input; file paths go through <see cref="PathGuard"/>
/// for traversal protection, using the same config as <c>FileOperationActivity</c>.
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
            var json = loaded.Content!;

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

    private Task<(string? Content, ActivityResult? Error)> LoadJsonAsync(string source, JsonElement config, CancellationToken ct)
        => QueryPayloadSource.LoadAsync(
            source,
            config,
            _config,
            MaxJsonBytes,
            Fail,
            (path, length) => $"file '{path}' is {length} bytes; exceeds limit of {MaxJsonBytes}.",
            ct);

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
