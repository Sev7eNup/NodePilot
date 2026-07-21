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

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);
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

    // Prefix captured in group 1, value in group 2. The whole match is replaced by "$1***".
    private static readonly Regex[] DefaultPatterns =
    {
        // key=value and key: value shapes — covers env-var dumps, config dumps, JSON output.
        // M-5: widened value class — commas are legal inside secret strings (e.g. base64 pad
        // regions, concatenated tokens), so only whitespace / semicolons / quotes terminate.
        new(@"((?:api[_-]?key|password|passwd|pwd|secret|token|bearer|access[_-]?key|client[_-]?secret|private[_-]?key|auth(?:orization)?|session[_-]?key|refresh[_-]?token|thumbprint|fingerprint)\s*[=:]\s*)([^\s;""']+)", RxOpts, RegexTimeout),
        // Double-quoted value shape: `password = "abc 123"` — previously slipped past the
        // bareword pattern above because the capture class excluded '"'. Audit L6.
        new(@"((?:api[_-]?key|password|passwd|pwd|secret|token|bearer|access[_-]?key|client[_-]?secret|private[_-]?key|auth(?:orization)?|session[_-]?key|refresh[_-]?token|thumbprint|fingerprint)\s*[=:]\s*"")([^""]*)", RxOpts, RegexTimeout),
        // Single-quoted value shape: `password = 'abc 123'` — PowerShell output form.
        new(@"((?:api[_-]?key|password|passwd|pwd|secret|token|bearer|access[_-]?key|client[_-]?secret|private[_-]?key|auth(?:orization)?|session[_-]?key|refresh[_-]?token|thumbprint|fingerprint)\s*[=:]\s*')([^']*)", RxOpts, RegexTimeout),
        // JSON string form: "password": "xxx"
        new(@"(""(?:api[_-]?key|password|passwd|pwd|secret|token|bearer|access[_-]?key|client[_-]?secret|private[_-]?key|authorization|session[_-]?key|refresh[_-]?token|thumbprint|fingerprint)""\s*:\s*"")([^""]*)", RxOpts, RegexTimeout),
        // Connection string segments
        new(@"(Password\s*=\s*)([^;]+)", RxOpts, RegexTimeout),
        new(@"(Pwd\s*=\s*)([^;]+)", RxOpts, RegexTimeout),
        new(@"(User\s*(?:Id|ID)\s*=\s*)([^;]+)", RxOpts, RegexTimeout),
        // HTTP header lines: "Authorization: Bearer xxx", "X-Api-Key: xxx"
        new(@"((?:Authorization|Proxy-Authorization|X-Api-Key|X-Auth-Token|X-Webhook-Secret|Cookie|Set-Cookie)\s*:\s*)([^\r\n]+)", RxOpts, RegexTimeout),
        // PEM-formatted keys (private key, certificate) — blank the body between markers
        new(@"(-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED |)PRIVATE KEY-----)[\s\S]*?(-----END (?:RSA |EC |DSA |OPENSSH |ENCRYPTED |)PRIVATE KEY-----)", RxOpts, RegexTimeout),
        // AWS access key IDs and GitHub tokens (shape-based — catches accidental Write-Host)
        new(@"\b(AKIA|ASIA)[A-Z0-9]{16}\b", RegexOptions.Compiled, RegexTimeout),
        new(@"\b(gh[pousr]_[A-Za-z0-9]{20,})\b", RegexOptions.Compiled, RegexTimeout),
        // M-5 widen the catch-all set:
        // JWT shape: 3 dot-separated base64url segments (at least 10 chars each).
        new(@"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.Compiled, RegexTimeout),
        // Stripe live/test keys
        new(@"sk_(live|test)_[A-Za-z0-9]{16,}", RegexOptions.Compiled, RegexTimeout),
        // Slack tokens (bot, user, access, refresh, etc.)
        new(@"xox[baprs]-[A-Za-z0-9-]{10,}", RegexOptions.Compiled, RegexTimeout),
        // GitLab personal access tokens
        new(@"glpat-[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled, RegexTimeout),
        // Any PEM block (certificate, public key, ...) — not just private key.
        new(@"(-----BEGIN [A-Z ]+-----)[\s\S]{1,40000}?(-----END [A-Z ]+-----)", RxOpts, RegexTimeout),
    };

    private readonly Regex[] _patterns;
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
            .ToArray();

        _patterns = DefaultPatterns.Concat(custom ?? Array.Empty<Regex>()).ToArray();
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
        // possibly match — if none are present, skip 16 compiled regex passes entirely.
        // This is the hot-path on every step's output; the workflow engine pipes every byte
        // of script output through here, and 99 %+ of typical lines have nothing to redact.
        if (!HasRedactionTrigger(input)) return input;
        var s = input;
        foreach (var rx in _patterns)
        {
            try
            {
                s = rx.Replace(s, m =>
                {
                    // Track every match so a sudden uptick is visible in dashboards. The pattern
                    // index keeps the metric cardinality bounded (one tag value per regex).
                    NodePilot.Engine.EngineMetrics.RedactionHits.Add(1,
                        new KeyValuePair<string, object?>("pattern_kind", PatternKind(m)));
                    // PEM-style pattern captures both start and end markers — keep them,
                    // blank the body in between.
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
                // M-4: fail-open rather than fail-closed. Nuking the entire string on a single
                // regex timeout destroys huge amounts of legitimate output (often many MBs of
                // script stdout) to defend against a speculative leak that the other patterns
                // likely already caught. We log a warning so ops can tune the offending pattern.
                _logger?.LogWarning(
                    "OutputRedactor: regex timeout on pattern {Pattern} (input length {Length} chars); preserving input.",
                    rx.ToString(), s.Length);
            }
        }
        return s;
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
        // Pattern keys in DefaultPatterns — match the full identifier set across all the
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
        if (_patterns.Length > DefaultPatterns.Length) return true;
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
