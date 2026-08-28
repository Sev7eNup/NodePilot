using System.Text.RegularExpressions;

namespace NodePilot.Core.WorkflowDefinitions;

/// <summary>
/// Content-based secret detection for workflow-definition string values, complementing the
/// key-name allowlist in <see cref="WorkflowSecretKeys"/>. Catches inline secrets in values whose
/// config key is not itself secret-named, such as a restApi <c>headers</c> string
/// (<c>Authorization: Bearer …</c>), a request <c>body</c>, or a runScript <c>script</c> that
/// hard-codes a token.
///
/// <para>A matching value is masked whole, never partially, so the merge layers
/// (<c>WorkflowDefinitionMerge</c> / <c>WorkflowDefinitionPatcher</c>) can restore it from the
/// unredacted original on edit. Detection covers credential-header lines, provider token shapes,
/// and quoted secret-name assignments; <c>{{…}}</c> template spans are stripped first so a
/// <c>{{globals.X}}</c> reference is never flagged as a literal secret.
/// <see cref="CredentialHeaderNames"/> is the source of truth for header names;
/// <see cref="PublicHttpHeaderNames"/> is the inverse allowlist for redirect forwarding.</para>
/// </summary>
public static class WorkflowSecretContent
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    /// <summary>
    /// Credential HTTP header names, the single source of truth consumed by generic content
    /// detection. Definition-aware HTTP header objects and strings additionally treat every
    /// non-public literal header as secret.
    /// </summary>
    public static readonly IReadOnlySet<string> CredentialHeaderNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
            "X-Api-Key", "X-Auth-Token", "X-Webhook-Secret",
        };

    /// <summary>
    /// HTTP request headers whose literal values are configuration metadata rather than
    /// credentials.
    /// Every other user-authored header is secret-bearing by default: custom authentication schemes
    /// deliberately do not have a universal naming convention.
    /// </summary>
    public static readonly IReadOnlySet<string> PublicHttpHeaderNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Accept", "Content-Type", "User-Agent",
        };

    public static bool IsPublicHttpHeader(string? name)
        => name is not null && PublicHttpHeaderNames.Contains(name);

    public static bool IsLiteralSecretHeaderValue(string? name, string? value)
    {
        if (string.IsNullOrEmpty(value) || IsPublicHttpHeader(name)) return false;

        var withoutTemplates = SafeReplace(TemplateSpan, value, " ");
        return SafeReplace(SchemePrefix, withoutTemplates, string.Empty).Trim().Length > 0;
    }

    /// <summary>
    /// True when a multi-line HTTP-header value contains a literal value in any header that is
    /// not explicitly public. Template-only values are references, not secrets stored in the
    /// workflow definition.
    /// </summary>
    public static bool ContainsLiteralSecretHeader(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        foreach (var line in value.Split('\n'))
        {
            var m = SafeMatch(HeaderLine, line);
            if (m is null) return true;
            if (m.Success && IsLiteralSecretHeaderValue(m.Groups[1].Value, m.Groups[2].Value))
                return true;
        }

        return false;
    }

    // Any {{…}} template span (globals/databus reference). Stripped before detection so a value
    // that only references a secret is never masked.
    private static readonly Regex TemplateSpan = new(@"\{\{[^}]*\}\}", RegexOptions.CultureInvariant, Timeout);

    // One `Key: Value` header line — group 1 = name, group 2 = value.
    private static readonly Regex HeaderLine = new(
        @"^\s*([!#$%&'*+.^_`|~A-Za-z0-9-]+)\s*:\s*(.+)$",
        RegexOptions.CultureInvariant,
        Timeout);

    // Leading auth scheme keyword ("Bearer <token>", "Basic <b64>", …) — dropped so a bare scheme
    // with a globals-referenced token (already template-stripped) doesn't read as a literal secret.
    private static readonly Regex SchemePrefix = new(@"^(?:Bearer|Basic|Digest|Negotiate|Token|ApiKey)\s+", Opts, Timeout);

    // Quoted secret-name assignment or JSON field: $token = "…", password: '…', "apiKey": "…"
    // (value ≥ 6 chars). The optional quote after the key name absorbs the JSON `"key":"value"`
    // shape. {{…}} spans are stripped first, so a `"apiKey": "{{globals.X}}"` reference does not
    // match.
    private static readonly Regex QuotedAssignment = new(
        @"(?:api[_-]?key|password|passwd|pwd|secret|token|bearer|access[_-]?key|client[_-]?secret|private[_-]?key|auth[_-]?token|refresh[_-]?token|session[_-]?key|webhook[_-]?secret)[""']?\s*[=:]\s*[""'][^""']{6,}[""']",
        Opts, Timeout);

    // Unambiguous provider token shapes (near-zero false positive).
    private static readonly Regex[] TokenShapes =
    {
        new(@"sk_(?:live|test)_[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant, Timeout),          // Stripe
        new(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b", RegexOptions.CultureInvariant, Timeout),              // AWS access key id
        new(@"\bgh[pousr]_[A-Za-z0-9]{20,}\b", RegexOptions.CultureInvariant, Timeout),             // GitHub
        new(@"\bglpat-[A-Za-z0-9_\-]{20,}\b", RegexOptions.CultureInvariant, Timeout),              // GitLab PAT
        new(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.CultureInvariant, Timeout),           // Slack
        new(@"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.CultureInvariant, Timeout), // JWT
        new(@"-----BEGIN (?:[A-Z]+ )*PRIVATE KEY-----", RegexOptions.CultureInvariant, Timeout),    // PEM private key
    };

    /// <summary>True when the string value carries an inline secret, regardless of its config key
    /// name.</summary>
    public static bool LooksSecret(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 6) return false;

        // The expected pattern is `{{globals.X}}` — a reference, never a literal secret. Strip all
        // template spans first so a header/body/script that only references a secret is not masked.
        var scrubbed = SafeReplace(TemplateSpan, value, " ");

        // (1) A credential HTTP header line whose value has a literal remainder (not just a scheme
        // word).
        foreach (var line in scrubbed.Split('\n'))
        {
            var m = SafeMatch(HeaderLine, line);
            if (m is null) return true;
            if (m.Success && CredentialHeaderNames.Contains(m.Groups[1].Value))
            {
                var remainder = SafeReplace(SchemePrefix, m.Groups[2].Value, string.Empty).Trim();
                if (remainder.Length >= 3) return true;
            }
        }

        // (2) Unambiguous provider token shapes anywhere.
        foreach (var rx in TokenShapes)
            if (SafeIsMatch(rx, scrubbed)) return true;

        // (3) A quoted secret-name assignment with a non-trivial literal value.
        return SafeIsMatch(QuotedAssignment, scrubbed);
    }

    // Regex helpers that fail closed: a pathological input that trips the timeout is treated as
    // suspicious (mask it) rather than silently passing through.
    private static bool SafeIsMatch(Regex rx, string input)
    {
        try { return rx.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return true; }
    }

    private static Match? SafeMatch(Regex rx, string input)
    {
        try { return rx.Match(input); }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static string SafeReplace(Regex rx, string input, string replacement)
    {
        try { return rx.Replace(input, replacement); }
        catch (RegexMatchTimeoutException) { return input; }
    }
}
