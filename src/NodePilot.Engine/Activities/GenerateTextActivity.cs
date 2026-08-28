using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Generates a cryptographically secure random string (IDs, tokens, GUIDs, password charsets).
/// Uses <see cref="RandomNumberGenerator"/> instead of <see cref="System.Random"/>, with
/// rejection sampling via <see cref="RandomNumberGenerator.GetInt32(int)"/> to avoid modulo bias.
/// The <c>password</c> mode is a charset preset only and does not guarantee one character per
/// class, so it is not a policy-compliant password generator. The result is not redacted in
/// <see cref="ActivityResult.Output"/>, because it is generated randomness, not an echoed secret.
/// </summary>
public sealed class GenerateTextActivity : IActivityExecutor
{
    public string ActivityType => "generateText";

    internal const int MinLength = 1;
    internal const int MaxLength = 1024;
    internal const int DefaultLength = 16;
    internal const string DefaultMode = "alphanumeric";

    internal const string Lower = "abcdefghijklmnopqrstuvwxyz";
    internal const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    internal const string Digits = "0123456789";
    // Curated shell-/URL-/quote-safe symbol set: no space, quotes, backslash, or shell/expansion
    // metacharacters, so a generated value pasted into a connection string, URL, or CLI arg needs
    // no escaping and can't break a downstream command.
    internal const string Symbols = "!#$%*+-=?@^_~";
    internal const string HexLower = "0123456789abcdef";

    // Visually confusable glyphs removed when excludeAmbiguous=true.
    internal const string AmbiguousChars = "0Oo1lIi5Ss2Zz8B6Gg9q|";

    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
        => ActivityExecution.RunAsync(() =>
        {
            var mode = ReadMode(config);

            if (mode == "guid")
                return Task.FromResult(Ok(Guid.NewGuid().ToString())); // "D" format, lowercase

            if (!TryBuildCharset(mode, config, out var charset, out var error))
                return Task.FromResult(Fail(error!));

            var text = GenerateFromCharset(charset, ReadLength(config));
            return Task.FromResult(Ok(text));
        }, ex => $"Generate Text error: {ex.Message}");

    // ---- config readers (internal static = unit-testable) ----

    /// <summary>
    /// Reads a string property only when it is genuinely a JSON string. Unlike
    /// <c>ConfigExtensions.GetStringOrNull</c>, which throws on non-string JSON (e.g. <c>mode:
    /// 5</c>),
    /// this stays tolerant so a malformed mode/customCharset falls back instead of failing the
    /// step.
    /// </summary>
    private static string? ReadString(JsonElement config, string key)
        => config.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    internal static string ReadMode(JsonElement config)
    {
        var mode = ReadString(config, "mode")?.Trim().ToLowerInvariant();
        return mode switch
        {
            "alphanumeric" or "alphabetic" or "numeric" or "hex"
                or "guid" or "password" or "custom" => mode,
            _ => DefaultMode,
        };
    }

    internal static int ReadLength(JsonElement config)
    {
        var raw = DefaultLength;
        if (config.TryGetProperty("length", out var s)
            && s.ValueKind == JsonValueKind.Number
            && s.TryGetInt32(out var parsed))
            raw = parsed;
        return Math.Clamp(raw, MinLength, MaxLength);
    }

    /// <summary>
    /// Builds the effective alphabet for non-guid modes. Returns false with an error message for
    /// an empty/whitespace custom charset or a preset emptied by excludeAmbiguous.
    /// </summary>
    internal static bool TryBuildCharset(string mode, JsonElement config, out string charset, out string? error)
    {
        error = null;

        if (mode == "custom")
        {
            var custom = ReadString(config, "customCharset");
            if (string.IsNullOrWhiteSpace(custom))
            {
                charset = string.Empty;
                error = "Generate Text: 'customCharset' is required and must contain at least one "
                      + "non-whitespace character for mode=custom";
                return false;
            }
            // Uniform selection: de-dupe so a repeated glyph isn't over-weighted.
            charset = Deduplicate(custom);
            return true;
        }

        var baseSet = mode switch
        {
            "alphanumeric" => Lower + Upper + Digits,
            "alphabetic" => Lower + Upper,
            "numeric" => Digits,
            "hex" => HexLower,
            "password" => Lower + Upper + Digits + Symbols,
            _ => Lower + Upper + Digits, // unreachable; ReadMode already normalized
        };

        if (config.GetBool("excludeAmbiguous", false)) // never touches custom/guid
            baseSet = RemoveAmbiguous(baseSet);

        if (baseSet.Length == 0)
        {
            charset = string.Empty;
            error = "Generate Text: character set is empty after removing ambiguous characters";
            return false;
        }

        charset = baseSet;
        return true;
    }

    internal static string Deduplicate(string s)
    {
        var seen = new HashSet<char>();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (seen.Add(c)) sb.Append(c);
        return sb.ToString();
    }

    internal static string RemoveAmbiguous(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (AmbiguousChars.IndexOf(c) < 0) sb.Append(c);
        return sb.ToString();
    }

    internal static string GenerateFromCharset(string charset, int length)
    {
        // Precondition: charset non-empty (validated by TryBuildCharset).
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = charset[RandomNumberGenerator.GetInt32(charset.Length)];
        return new string(chars);
    }

    // ---- result shaping ----

    private static ActivityResult Ok(string text) => new()
    {
        Success = true,
        Output = text,
        OutputParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = text,
            ["length"] = text.Length.ToString(),
        },
    };

    private static ActivityResult Fail(string error) => new()
    {
        Success = false,
        ErrorOutput = error,
    };
}
