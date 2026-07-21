using System.Text.RegularExpressions;

namespace NodePilot.Core.WorkflowDefinitions;

/// <summary>
/// Content-based secret detection for workflow-definition string values, complementing the
/// key-name allowlist in <see cref="WorkflowSecretKeys"/>. It catches inline secrets that live in
/// values whose config key is <b>not</b> itself secret-named — most importantly a restApi
/// <c>headers</c> string (<c>Authorization: Bearer …</c>), a request <c>body</c>, or a runScript
/// <c>script</c> that hard-codes a token.
///
/// A matching value is masked/encrypted <b>whole</b> (never partially), so the redact→edit
/// round-trip stays intact: the merge layers (<c>WorkflowDefinitionMerge</c> /
/// <c>WorkflowDefinitionPatcher</c>) restore a whole-<c>"***"</c> value from the unredacted
/// original. Detection is deliberately high-signal — credential-header lines, unambiguous provider
/// token shapes, and quoted secret-name assignments — and it first strips <c>{{…}}</c> template
/// spans so the steered <c>{{globals.X}}</c> reference (a pointer, never a literal secret) is not
/// flagged. False positives only cost redacted-view visibility, never data.
///
/// The provider token shapes and credential-header names mirror the runtime
/// <c>OutputRedactor</c> vocabulary; <see cref="CredentialHeaderNames"/> is the single source of
/// truth also consumed by <c>RestApiActivity</c>.
/// </summary>
public static class WorkflowSecretContent
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    /// <summary>
    /// Credential HTTP header names — the single source of truth, consumed by
    /// <c>RestApiActivity</c> (redirect stripping) and this detector (headers-string masking).
    /// </summary>
    public static readonly IReadOnlySet<string> CredentialHeaderNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
            "X-Api-Key", "X-Auth-Token", "X-Webhook-Secret",
        };

    // Any {{…}} template span (globals/databus reference). Stripped before detection so a value
    // that only *references* a secret is never masked.
    private static readonly Regex TemplateSpan = new(@"\{\{[^}]*\}\}", RegexOptions.CultureInvariant, Timeout);

    // One `Key: Value` header line — group 1 = name, group 2 = value.
    private static readonly Regex HeaderLine = new(@"^\s*([A-Za-z][A-Za-z0-9\-]*)\s*:\s*(.+)$", RegexOptions.CultureInvariant, Timeout);

    // Leading auth scheme keyword ("Bearer <token>", "Basic <b64>", …) — dropped so a bare scheme
    // with a globals-referenced token (already template-stripped) doesn't read as a literal secret.
    private static readonly Regex SchemePrefix = new(@"^(?:Bearer|Basic|Digest|Negotiate|Token|ApiKey)\s+", Opts, Timeout);

    // Quoted secret-name assignment or JSON field: $token = "…", password: '…', "apiKey": "…"
    // (value ≥ 6 chars). The optional quote after the key name absorbs the JSON `"key":"value"`
    // shape. {{…}} spans are stripped first, so a `"apiKey": "{{globals.X}}"` reference does not match.
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

    /// <summary>True when the string value carries an inline secret, regardless of its config key name.</summary>
    public static bool LooksSecret(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 6) return false;

        // The steered pattern is `{{globals.X}}` — a reference, never a literal secret. Remove all
        // template spans first so a header/body/script that only references a secret is not masked.
        var scrubbed = SafeReplace(TemplateSpan, value, " ");

        // (1) A credential HTTP header line whose value has a literal remainder (not just a scheme word).
        foreach (var line in scrubbed.Split('\n'))
        {
            var m = SafeMatch(HeaderLine, line);
            if (m is { Success: true } && CredentialHeaderNames.Contains(m.Groups[1].Value))
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
