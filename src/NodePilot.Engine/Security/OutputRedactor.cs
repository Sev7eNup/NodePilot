using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Audit;
using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Security;

/// <summary>
/// Regex-based redaction for script stdout/stderr, output parameters, and log messages
/// before they leave the engine (DB persist, SignalR broadcast, step-detail log, API
/// response). Not bullet-proof — a motivated script can still encode secrets past these
/// patterns — but catches the common careless cases where a workflow author writes
/// <c>Write-Host "pwd=$password"</c> and then stares at the audit trail confused.
///
/// Built-in patterns cover the common key=value, JSON, header and PEM shapes. Operators
/// can extend the set via <c>Logging:Redaction:Patterns</c> — each entry is a full .NET
/// regex and its first capturing group is preserved, the rest replaced by <c>***</c>.
///
/// Redaction is always-on: the <c>Logging:Redaction:Enabled</c> knob only exists for the
/// test suite and is ignored outside the <c>Testing</c> environment. A misconfiguration
/// cannot disable secret scrubbing in production.
/// </summary>
public sealed class OutputRedactor : IAuditDetailsRedactor
{
    public const string Placeholder = "***";

    // For operator patterns and the name splitter only. A redaction pass that times out is
    // skipped, and a wall-clock timeout also fires on a thread that merely got no CPU, so the
    // built-in passes are linear by construction and run without one.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan NoTimeout = Regex.InfiniteMatchTimeout;
    private static readonly RegexOptions RxOpts = RegexOptions.Compiled | RegexOptions.IgnoreCase;

    private static readonly Regex SensitiveNameWordBoundary = new(
        @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|[^A-Za-z0-9]+",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly HashSet<string> SensitiveNameWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "pwd", "secret", "bearer", "authorization",
        "cookie", "credential", "credentials", "signature",
    };

    private static readonly HashSet<string> SensitiveNameCompounds = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey", "accesstoken", "refreshtoken", "sessiontoken", "authtoken",
        "bearertoken", "idtoken", "clientsecret", "privatekey", "accesskey",
        "sessionkey", "signingkey", "encryptionkey", "hmackey", "jwtkey",
        "connectionstring", "webhooksecret", "webhooksignature",
    };

    private static readonly HashSet<string> SensitiveKeyQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "access", "client", "private", "session", "signing", "encryption",
        "webhook", "hmac", "jwt", "secret",
    };

    // "(BEGIN)[\s\S]{MinBody,MaxBody}?(END)" without the regex: retried from every BEGIN marker,
    // the lazy body is quadratic under backtracking. Same matches, linear time.
    private sealed record PemBlocks(Regex Begin, Regex End, int MinBody, int MaxBody);

    // PEM-formatted private keys — blank the body between markers.
    private static readonly PemBlocks PrivateKeyPem = new(
        new(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED |)PRIVATE KEY-----", RxOpts, NoTimeout),
        new(@"-----END (?:RSA |EC |DSA |OPENSSH |ENCRYPTED |)PRIVATE KEY-----", RxOpts, NoTimeout),
        MinBody: 0, MaxBody: int.MaxValue);

    // Any PEM block (certificate, public key, ...) — not just private key.
    private static readonly PemBlocks AnyPem = new(
        new(@"-----BEGIN [A-Z ]+-----", RxOpts, NoTimeout),
        new(@"-----END [A-Z ]+-----", RxOpts, NoTimeout),
        MinBody: 1, MaxBody: 40_000);

    // Applied in order; each pass sees the output of the one before. A Regex keeps its group 1
    // prefix and replaces the rest with "***". None of these patterns nests quantifiers.
    private static readonly object[] DefaultPasses =
    {
        // key=value and key: value shapes — covers env-var dumps, config dumps, JSON output.
        // M-5: widened value class — commas are legal inside secret strings (e.g. base64 pad
        // regions, concatenated tokens), so only whitespace / semicolons / quotes terminate.
        new Regex(@"((?:api[_-]?key|password|passwd|pwd|secret|token|bearer|access[_-]?key|client[_-]?secret|private[_-]?key|auth(?:orization)?|session[_-]?key|refresh[_-]?token|thumbprint|fingerprint)\s*[=:]\s*)([^\s;""']+)", RxOpts, NoTimeout),
        // Match double-quoted values separately because the bareword pattern excludes quotes.
        new Regex(@"((?:api[_-]?key|password|passwd|pwd|secret|token|bearer|access[_-]?key|client[_-]?secret|private[_-]?key|auth(?:orization)?|session[_-]?key|refresh[_-]?token|thumbprint|fingerprint)\s*[=:]\s*"")([^""]*)", RxOpts, NoTimeout),
        // Single-quoted value shape: `password = 'abc 123'` — PowerShell output form.
        new Regex(@"((?:api[_-]?key|password|passwd|pwd|secret|token|bearer|access[_-]?key|client[_-]?secret|private[_-]?key|auth(?:orization)?|session[_-]?key|refresh[_-]?token|thumbprint|fingerprint)\s*[=:]\s*')([^']*)", RxOpts, NoTimeout),
        // JSON string form: "password": "xxx"
        new Regex(@"(""(?:api[_-]?key|password|passwd|pwd|secret|token|bearer|access[_-]?key|client[_-]?secret|private[_-]?key|authorization|session[_-]?key|refresh[_-]?token|thumbprint|fingerprint)""\s*:\s*"")([^""]*)", RxOpts, NoTimeout),
        // Connection string segments
        new Regex(@"(Password\s*=\s*)([^;]+)", RxOpts, NoTimeout),
        new Regex(@"(Pwd\s*=\s*)([^;]+)", RxOpts, NoTimeout),
        new Regex(@"(User\s*(?:Id|ID)\s*=\s*)([^;]+)", RxOpts, NoTimeout),
        // HTTP header lines: "Authorization: Bearer xxx", "X-Api-Key: xxx"
        new Regex(@"((?:Authorization|Proxy-Authorization|X-Api-Key|X-Auth-Token|X-Webhook-Secret|Cookie|Set-Cookie)\s*:\s*)([^\r\n]+)", RxOpts, NoTimeout),
        PrivateKeyPem,
        // AWS access key IDs and GitHub tokens (shape-based — catches accidental Write-Host)
        new Regex(@"\b(AKIA|ASIA)[A-Z0-9]{16}\b", RegexOptions.Compiled, NoTimeout),
        new Regex(@"\b(gh[pousr]_[A-Za-z0-9]{20,})\b", RegexOptions.Compiled, NoTimeout),
        // M-5 widen the catch-all set:
        // JWT shape: 3 dot-separated base64url segments (at least 10 chars each).
        new Regex(@"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.Compiled, NoTimeout),
        // Stripe live/test keys
        new Regex(@"sk_(live|test)_[A-Za-z0-9]{16,}", RegexOptions.Compiled, NoTimeout),
        // Slack tokens (bot, user, access, refresh, etc.)
        new Regex(@"xox[baprs]-[A-Za-z0-9-]{10,}", RegexOptions.Compiled, NoTimeout),
        // GitLab personal access tokens
        new Regex(@"glpat-[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled, NoTimeout),
        AnyPem,
    };

    private readonly object[] _passes;
    private readonly bool _hasCustomPatterns;
    private readonly bool _enabled;
    private readonly ILogger<OutputRedactor>? _logger;

    public OutputRedactor(IConfiguration? configuration = null, ILogger<OutputRedactor>? logger = null)
    {
        _logger = logger;
        // Disabling redaction is only honored in Testing. In every other environment the
        // switch is ignored so a misconfigured appsettings.json cannot silently leak.
        var envName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                   ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var isTesting = string.Equals(envName, "Testing", StringComparison.OrdinalIgnoreCase);
        _enabled = !isTesting
            || (configuration?.GetValue("Logging:Redaction:Enabled", true) ?? true);

        var custom = configuration?.GetSection("Logging:Redaction:Patterns").GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .Select(p => { try { return new Regex(p, RxOpts, RegexTimeout); } catch { return null!; } })
            .Where(r => r is not null)
            .ToArray() ?? [];

        _hasCustomPatterns = custom.Length > 0;
        _passes = [.. DefaultPasses, .. custom];
    }

    /// <summary>
    /// Apply all patterns to the input. Returns null/empty unchanged. Matches with no capture
    /// group are replaced wholesale; matches with a group 1 keep the prefix and blank the rest.
    /// </summary>
    public string? Redact(string? input)
    {
        if (!_enabled || string.IsNullOrEmpty(input)) return input;
        // Fast-path: a script's stdout/stderr almost never contains a secret. Probe for the
        // hand-full of marker substrings and PEM/token shapes that any of our patterns can
        // possibly match — if none are present, skip every pass entirely.
        // This is the hot-path on every step's output; the workflow engine pipes every byte
        // of script output through here, and 99 %+ of typical lines have nothing to redact.
        if (!HasRedactionTrigger(input)) return input;
        var s = input;
        foreach (var pass in _passes)
            s = pass is PemBlocks pem ? RedactPemBlocks(s, pem) : ApplyPattern((Regex)pass, s);
        return s;
    }

    private string ApplyPattern(Regex rx, string s)
    {
        try
        {
            return rx.Replace(s, m =>
            {
                // Track every match so a sudden uptick is visible in dashboards. The pattern
                // index keeps the metric cardinality bounded (one tag value per regex).
                NodePilot.Engine.EngineMetrics.RedactionHits.Add(1,
                    new KeyValuePair<string, object?>("pattern_kind", PatternKind(m)));
                // An operator's PEM-style pattern captures both markers — keep them, blank the
                // body in between.
                if (m.Groups.Count > 2 && m.Groups[1].Success && m.Groups[2].Success
                    && m.Groups[1].Value.StartsWith("-----BEGIN", StringComparison.OrdinalIgnoreCase))
                    return m.Groups[1].Value + Placeholder + m.Groups[2].Value;
                if (m.Groups.Count > 1 && m.Groups[1].Success)
                    return m.Groups[1].Value + Placeholder;
                return Placeholder;
            });
        }
        catch (RegexMatchTimeoutException)
        {
            // Only operator patterns carry a timeout. M-4: fail-open rather than fail-closed.
            // Nuking the entire string on a single regex timeout destroys huge amounts of
            // legitimate output to defend against a leak the other patterns likely caught.
            _logger?.LogWarning(
                "OutputRedactor: regex timeout on pattern {Pattern} (input length {Length} chars); preserving input.",
                rx.ToString(), s.Length);
            return s;
        }
    }

    // Each BEGIN pairs with the first END that leaves a body within bounds; otherwise the next
    // BEGIN is tried. END candidates only move forward, so one search can serve many BEGINs.
    private static string RedactPemBlocks(string s, PemBlocks pem)
    {
        StringBuilder? sb = null;
        var copied = 0;
        Match? end = null;
        var begin = pem.Begin.Match(s);
        while (begin.Success)
        {
            var bodyStart = begin.Index + begin.Length;
            var from = bodyStart + pem.MinBody;
            if (end is null || (end.Success && end.Index < from))
                end = from <= s.Length ? pem.End.Match(s, from) : Match.Empty;
            if (end.Success && end.Index - bodyStart <= pem.MaxBody)
            {
                NodePilot.Engine.EngineMetrics.RedactionHits.Add(1,
                    new KeyValuePair<string, object?>("pattern_kind", "pem"));
                sb ??= new StringBuilder(s.Length);
                sb.Append(s, copied, begin.Index - copied).Append(begin.Value).Append(Placeholder).Append(end.Value);
                copied = end.Index + end.Length;
                begin = pem.Begin.Match(s, copied);
            }
            else
            {
                begin = pem.Begin.Match(s, begin.Index + 1);
            }
        }
        return sb is null ? s : sb.Append(s, copied, s.Length - copied).ToString();
    }

    /// <summary>
    /// Length cap for untrusted text before it lands in a DB row or a log line, so a single
    /// leaked blob can't blow the row or the audit log. The <c>"... [truncated]"</c> marker is
    /// part of the persisted value — the inspector UI shows it as "this was truncated".
    /// </summary>
    public static string Cap(string value, int maxChars)
        => value.Length > maxChars ? value.Substring(0, maxChars) + "... [truncated]" : value;

    /// <summary>
    /// Apply the redactor to untrusted text and cap the length. Used for ErrorMessage,
    /// InputParametersJson, ReturnData.
    /// </summary>
    public string? RedactAndCap(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var redacted = Redact(value) ?? value;
        return Cap(redacted, maxChars);
    }

    /// <summary>
    /// Redacts a value with awareness of the field/variable name. Value-only regexes cannot
    /// recognize opaque secrets such as <c>dbPassword = hunter2</c> once the key and value have
    /// been split into an <c>OutputParameters</c> dictionary. Qualified names and camel/Pascal
    /// case are tokenized, so names such as <c>step.param.clientSecret</c> and
    /// <c>webhookHeader_X-NodePilot-Signature</c> are protected as well.
    /// </summary>
    public string? RedactNamedValue(string? name, string? value)
    {
        if (!_enabled)
            return value;

        if (IsSensitiveName(name))
        {
            EngineMetrics.RedactionHits.Add(1,
                new KeyValuePair<string, object?>("pattern_kind", "sensitive_name"));
            return Placeholder;
        }

        return Redact(value);
    }

    internal static bool IsSensitiveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        string[] words;
        try
        {
            words = SensitiveNameWordBoundary.Split(name)
                .Where(static word => word.Length > 0)
                .ToArray();
        }
        catch (RegexMatchTimeoutException)
        {
            // An attacker-controlled, pathological key must not bypass redaction or fail the
            // workflow persistence path. Treat an unclassifiable name as sensitive.
            return true;
        }

        if (words.Any(word => SensitiveNameWords.Contains(word)
                              || SensitiveNameCompounds.Contains(word)))
        {
            return true;
        }

        for (var i = 0; i < words.Length; i++)
        {
            // Singular `token` denotes a credential. Plural telemetry counters such as
            // promptTokens/completionTokens intentionally remain visible.
            if (words[i].Equals("token", StringComparison.OrdinalIgnoreCase))
                return true;

            if (words[i].Equals("jwt", StringComparison.OrdinalIgnoreCase)
                && (words.Length == 1
                    || (i + 1 < words.Length
                        && (words[i + 1].Equals("key", StringComparison.OrdinalIgnoreCase)
                            || words[i + 1].Equals("secret", StringComparison.OrdinalIgnoreCase)
                            || words[i + 1].Equals("token", StringComparison.OrdinalIgnoreCase)
                            || words[i + 1].Equals("signature", StringComparison.OrdinalIgnoreCase)))))
            {
                return true;
            }

            if (words[i].Equals("key", StringComparison.OrdinalIgnoreCase)
                && i > 0
                && SensitiveKeyQualifiers.Contains(words[i - 1]))
            {
                return true;
            }

            if (words[i].Equals("auth", StringComparison.OrdinalIgnoreCase)
                && i + 1 < words.Length
                && (words[i + 1].Equals("header", StringComparison.OrdinalIgnoreCase)
                    || words[i + 1].Equals("token", StringComparison.OrdinalIgnoreCase)
                    || words[i + 1].Equals("secret", StringComparison.OrdinalIgnoreCase)
                    || words[i + 1].Equals("credential", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (words[i].Equals("connection", StringComparison.OrdinalIgnoreCase)
                && i + 1 < words.Length
                && words[i + 1].Equals("string", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Substrings that any of the default patterns can match. Done as an ordinal IndexOf
    // chain — measured ~5x faster than a single Regex with alternation on hot-path strings
    // that contain none of them, because IndexOf is vectorized and short-circuits on the
    // first hit. Custom patterns from Logging:Redaction:Patterns force the slow path
    // (we don't know what they trigger on).
    private static readonly string[] DefaultTriggerKeywords =
    {
        // Pattern keys in DefaultPasses — match the full identifier set across all the
        // key=value / JSON / header patterns. Case-insensitive, so check both lower- and
        // mixed-case forms. Includes the AKIA/ASIA/eyJ/sk_/xox/glpat/gh_ shape prefixes
        // and the "-----BEGIN" PEM marker.
        "password", "passwd", "pwd", "secret", "token", "bearer",
        "api_key", "api-key", "apikey",
        "access_key", "access-key", "accesskey",
        "client_secret", "client-secret", "clientsecret",
        "private_key", "private-key", "privatekey",
        "auth", "session", "refresh", "thumbprint", "fingerprint",
        "User Id", "User ID", "userid",
        "Authorization", "Proxy-Authorization", "X-Api-Key", "X-Auth-Token",
        "X-Webhook-Secret", "Cookie", "Set-Cookie",
        "-----BEGIN",
        "AKIA", "ASIA", "eyJ", "sk_live_", "sk_test_", "xox", "glpat-", "ghp_", "ghs_",
        "gho_", "ghu_", "ghr_",
    };

    private bool HasRedactionTrigger(string input)
    {
        // Custom patterns may match anything — can't safely fast-path past them.
        if (_hasCustomPatterns) return true;
        foreach (var keyword in DefaultTriggerKeywords)
        {
            if (input.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Cheap classification of which pattern matched, used as the OTel tag value.
    /// We classify on the matched text (not the regex source) so the cardinality stays
    /// bounded even when operators add custom patterns via Logging:Redaction:Patterns.
    /// </summary>
    private static string PatternKind(Match m)
    {
        var head = m.Value;
        if (head.Length > 0 && head.StartsWith("-----BEGIN", StringComparison.OrdinalIgnoreCase)) return "pem";
        if (head.StartsWith("eyJ", StringComparison.Ordinal)) return "jwt";
        if (head.StartsWith("AKIA", StringComparison.Ordinal) || head.StartsWith("ASIA", StringComparison.Ordinal)) return "aws_key";
        if (head.StartsWith("gh", StringComparison.Ordinal)) return "github_token";
        if (head.StartsWith("xox", StringComparison.Ordinal)) return "slack_token";
        if (head.StartsWith("glpat", StringComparison.Ordinal)) return "gitlab_pat";
        if (head.StartsWith("sk_", StringComparison.Ordinal)) return "stripe_key";
        if (m.Groups.Count > 1 && m.Groups[1].Success)
        {
            var prefix = m.Groups[1].Value;
            if (prefix.Contains(':')) return "header";
            if (prefix.Contains('=') || prefix.Contains(':')) return "kv";
        }
        return "custom";
    }

    /// <summary>
    /// Redacts both <c>Output</c> and <c>ErrorOutput</c>. Returns a copy; leaves the input
    /// result untouched so callers can still access the raw value for metrics/cancellation.
    /// </summary>
    public ActivityResult Redact(ActivityResult result)
    {
        // Also run OutputParameters through the redactor. Auto-capture in runScript exposes
        // every user-declared variable as a param; a careless `$apiKey = "..."` would otherwise
        // land in the DB and downstream variables unmasked.
        var redactedParams = result.OutputParameters;
        if (_enabled && result.OutputParameters is { Count: > 0 })
        {
            redactedParams = new Dictionary<string, string>(result.OutputParameters.Count);
            foreach (var (k, v) in result.OutputParameters)
                redactedParams[k] = RedactNamedValue(k, v) ?? v;
        }

        return new ActivityResult
        {
            Success = result.Success,
            Output = Redact(result.Output),
            ErrorOutput = Redact(result.ErrorOutput),
            Duration = result.Duration,
            OutputParameters = redactedParams,
            // Transcript captures full command echoes + their outputs — exactly the surface
            // where careless `Write-Host "pwd=$secret"` or `Get-Content secrets.json` lands
            // verbatim. Same redaction pass as Output/ErrorOutput so the tracing channel
            // can't be a side door around the existing protection.
            TraceOutput = Redact(result.TraceOutput),
            // Provenance is engine metadata (definition key/version/hash), not user output — carry
            // it through unchanged. Rebuilding the result would otherwise drop the custom-activity
            // reproducibility snapshot before StepRunner persists it.
            CustomActivity = result.CustomActivity,
        };
    }
}
