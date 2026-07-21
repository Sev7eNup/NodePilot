using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Management.Automation.Language;
using Microsoft.Extensions.Logging;
using NodePilot.Engine.Execution;

namespace NodePilot.Engine.PowerShell;

/// <summary>
/// Pure, side-effect-free primitives shared by <c>RunScriptActivity</c> and
/// <c>CustomActivityExecutor</c> (a custom activity is a reusable runScript preset): PowerShell-safe
/// <c>{{...}}</c> resolution into the script text, the marker-block parser that splits raw stdout
/// into clean output / transcript / output-parameters / exit code, and the error-based success rule.
/// Extracted from RunScriptActivity so both code paths stay byte-for-byte identical.
/// </summary>
internal static class PowerShellActivitySupport
{
    public const string TranscriptStartMarker = "###NODEPILOT_TRANSCRIPT_START###";
    public const string TranscriptEndMarker = "###NODEPILOT_TRANSCRIPT_END###";

    private const string ParamsMarker = PowerShellScriptWrapper.ParamsMarker;
    private const string ErrorMarker = PowerShellScriptWrapper.ErrorMarker;
    private const string ExitCodeMarker = PowerShellScriptWrapper.ExitCodeMarker;

    /// <summary>
    /// Resolves <c>{{globals.NAME}}</c> and <c>{{varName.output|error|success|param.x}}</c> in script
    /// text. In code context textual values become single-quoted PowerShell literals; inside an
    /// existing PowerShell string/here-string only the string content is escaped. Booleans become
    /// <c>$true</c>/<c>$false</c>. Unresolved references are left verbatim.
    /// </summary>
    public static string ResolveScriptVariables(string script, Dictionary<string, string> variables)
    {
        if (!script.Contains("{{"))
            return script;

        // Do not infer PowerShell lexical context with a hand-written quote scanner. In
        // particular, an apostrophe inside a line/block comment is not a string delimiter;
        // treating it as one caused the following untrusted template value to be inserted as
        // executable code. Tokenize a same-length, neutral surrogate through PowerShell's own
        // parser instead, then apply replacements from right to left so token offsets remain
        // valid. Ambiguous/invalid template extents fail closed before the script is executed.
        var expressions = FindTemplateExpressions(script);
        if (expressions.Count == 0)
            return script;

        var contexts = AnalyzeTemplateContexts(script, expressions);
        var resolved = new StringBuilder(script);
        for (var i = expressions.Count - 1; i >= 0; i--)
        {
            var expression = expressions[i];
            var context = contexts[i];

            // Templates in comments have no runtime meaning. Leaving them verbatim also
            // prevents a value containing a newline or comment terminator from becoming code.
            if (context == TemplateContext.Comment)
                continue;

            var replacement = ResolveTemplateExpression(expression, variables, context);
            if (replacement is null)
                continue;

            resolved.Remove(expression.Index, expression.Length);
            resolved.Insert(expression.Index, replacement);
        }

        return resolved.ToString();
    }

    private enum TemplateContext
    {
        Code,
        SingleQuotedString,
        DoubleQuotedString,
        SingleQuotedHereString,
        DoubleQuotedHereString,
        ExpandableStringSubexpression,
        Comment,
    }

    private sealed record TemplateExpression(Match Match, bool IsGlobal)
    {
        public int Index => Match.Index;
        public int Length => Match.Length;
    }

    private static List<TemplateExpression> FindTemplateExpressions(string script)
    {
        var expressions = new List<TemplateExpression>();
        expressions.AddRange(VariableResolver.GlobalsPattern.Matches(script)
            .Cast<Match>()
            .Select(match => new TemplateExpression(match, IsGlobal: true)));
        expressions.AddRange(VariableResolver.StepPattern.Matches(script)
            .Cast<Match>()
            .Select(match => new TemplateExpression(match, IsGlobal: false)));
        expressions.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        return expressions;
    }

    private static TemplateContext[] AnalyzeTemplateContexts(
        string script,
        IReadOnlyList<TemplateExpression> expressions)
    {
        var surrogate = script.ToCharArray();
        foreach (var expression in expressions)
        {
            // A run of identifier characters is lexically neutral in code and cannot close
            // strings/comments. Keeping the exact length preserves every source offset.
            Array.Fill(surrogate, 'x', expression.Index, expression.Length);
        }

        _ = Parser.ParseInput(new string(surrogate), out var tokens, out var parseErrors);
        var contexts = new TemplateContext[expressions.Count];

        for (var i = 0; i < expressions.Count; i++)
        {
            var expression = expressions[i];
            var end = expression.Index + expression.Length;

            var overlappingError = parseErrors.FirstOrDefault(error =>
                error.Extent.StartOffset < end && error.Extent.EndOffset > expression.Index);
            if (overlappingError is not null)
            {
                throw new InvalidOperationException(
                    $"PowerShell template at offset {expression.Index} has an unsafe or ambiguous syntax context: " +
                    overlappingError.Message);
            }

            // Prefer the narrowest covering token. Expandable strings can contain nested
            // subexpression tokens; a template in `$()` is code, while a direct template in
            // the surrounding string is string content.
            var flattenedTokens = FlattenTokens(tokens).ToArray();
            var token = flattenedTokens
                .Where(candidate => candidate.Extent.StartOffset <= expression.Index
                                    && candidate.Extent.EndOffset >= end)
                .OrderBy(candidate => candidate.Extent.EndOffset - candidate.Extent.StartOffset)
                .FirstOrDefault();

            if (token is null)
            {
                throw new InvalidOperationException(
                    $"PowerShell template at offset {expression.Index} has no unambiguous parser token context.");
            }

            var context = token.Kind switch
            {
                TokenKind.Comment => TemplateContext.Comment,
                TokenKind.HereStringLiteral => TemplateContext.SingleQuotedHereString,
                TokenKind.HereStringExpandable => TemplateContext.DoubleQuotedHereString,
                TokenKind.StringLiteral when token.Text.StartsWith('\'') => TemplateContext.SingleQuotedString,
                TokenKind.StringExpandable when token.Text.StartsWith('"') => TemplateContext.DoubleQuotedString,
                _ => TemplateContext.Code,
            };

            // Inside `$()` PowerShell performs another parse whose terminator scan is not
            // safely handled by merely single-quoting arbitrary content (a `)` in the value
            // can terminate that scan). Use a fixed expression over base64 data instead; the
            // attacker controls only the base64 literal, never PowerShell syntax.
            if (context == TemplateContext.Code
                && flattenedTokens.Any(candidate =>
                    !ReferenceEquals(candidate, token)
                    && candidate.Extent.StartOffset <= expression.Index
                    && candidate.Extent.EndOffset >= end
                    && candidate.Kind is TokenKind.StringExpandable or TokenKind.HereStringExpandable))
            {
                context = TemplateContext.ExpandableStringSubexpression;
            }

            contexts[i] = context;
        }

        return contexts;
    }

    private static IEnumerable<Token> FlattenTokens(IEnumerable<Token> tokens)
    {
        foreach (var token in tokens)
        {
            yield return token;
            if (token is not StringExpandableToken { NestedTokens: { } nestedTokens })
                continue;

            foreach (var nestedToken in FlattenTokens(nestedTokens))
                yield return nestedToken;
        }
    }

    private static string? ResolveTemplateExpression(
        TemplateExpression expression,
        IReadOnlyDictionary<string, string> variables,
        TemplateContext context)
    {
        var match = expression.Match;
        if (expression.IsGlobal)
        {
            var key = $"globals.{match.Groups[1].Value}";
            return variables.TryGetValue(key, out var globalValue)
                ? FormatResolvedValue(globalValue, context)
                : null;
        }

        var varName = match.Groups[1].Value;
        var property = match.Groups[2].Value;
        string? value = null;

        if (property.StartsWith("param.", StringComparison.Ordinal) && match.Groups[3].Success)
        {
            var paramName = match.Groups[3].Value;
            value = variables.GetValueOrDefault($"{varName}.param.{paramName}")
                 ?? variables.GetValueOrDefault(paramName);
        }
        else if (property == "output")
        {
            value = variables.GetValueOrDefault($"{varName}.output");
        }
        else if (property == "error")
        {
            value = variables.GetValueOrDefault($"{varName}.error");
        }
        else if (property == "success")
        {
            var raw = variables.GetValueOrDefault($"{varName}.success");
            return raw is null
                ? null
                : string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ? "$true" : "$false";
        }

        return value is null ? null : FormatResolvedValue(value, context);
    }

    private static string FormatResolvedValue(string value, TemplateContext context)
        => context switch
        {
            TemplateContext.SingleQuotedString => EscapeSingleQuotedContent(value),
            TemplateContext.DoubleQuotedString => EscapeDoubleQuotedContent(value),
            TemplateContext.SingleQuotedHereString => EscapeSingleQuotedHereStringContent(value),
            TemplateContext.DoubleQuotedHereString => EscapeDoubleQuotedContent(value),
            TemplateContext.ExpandableStringSubexpression => FormatEncodedExpression(value),
            _ => $"'{EscapeSingleQuotedContent(value)}'",
        };

    private static string FormatEncodedExpression(string value)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        return $"[System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{base64}'))";
    }

    private static string EscapeSingleQuotedContent(string value)
        => value.Replace("'", "''");

    private static string EscapeDoubleQuotedContent(string value)
        => value
            .Replace("`", "``")
            .Replace("$", "`$")
            .Replace("\"", "`\"");

    private static string EscapeSingleQuotedHereStringContent(string value)
    {
        if (!value.Contains("'@", StringComparison.Ordinal))
            return value;

        StringBuilder? sb = null;
        for (var i = 0; i < value.Length; i++)
        {
            if (IsHereStringTerminatorInValue(value, i, '\''))
            {
                sb ??= new StringBuilder(value.Length + 4).Append(value, 0, i);
                sb.Append(' ');
            }

            sb?.Append(value[i]);
        }

        return sb?.ToString() ?? value;
    }

    private static bool IsHereStringTerminatorInValue(string value, int index, char quote)
        => IsAtLineStart(value, index)
           && index + 1 < value.Length
           && value[index] == quote
           && value[index + 1] == '@'
           && (index + 2 == value.Length || value[index + 2] is '\r' or '\n');

    private static bool IsAtLineStart(string script, int index)
        => index == 0 || script[index - 1] is '\r' or '\n';

    /// <summary>
    /// Resolves <c>{{...}}</c> references against the flat step-variables dict and returns the RAW
    /// value (no quoting). Used for custom-activity input values, which are then injected via the
    /// wrapper (which applies the single-quote escaping). Unresolved references are left verbatim.
    /// </summary>
    public static string ResolveTemplateRaw(string text, IReadOnlyDictionary<string, string> variables)
    {
        if (!text.Contains("{{")) return text;

        text = VariableResolver.GlobalsPattern.Replace(text, m =>
        {
            var key = $"globals.{m.Groups[1].Value}";
            return variables.TryGetValue(key, out var v) ? v : m.Value;
        });

        return VariableResolver.StepPattern.Replace(text, match =>
        {
            var varName = match.Groups[1].Value;
            var property = match.Groups[2].Value;
            string? value = null;
            if (property.StartsWith("param.") && match.Groups[3].Success)
            {
                var pn = match.Groups[3].Value;
                value = variables.GetValueOrDefault($"{varName}.param.{pn}") ?? variables.GetValueOrDefault(pn);
            }
            else if (property == "output") value = variables.GetValueOrDefault($"{varName}.output");
            else if (property == "error") value = variables.GetValueOrDefault($"{varName}.error");
            else if (property == "success") value = variables.GetValueOrDefault($"{varName}.success");
            return value ?? match.Value;
        });
    }

    /// <summary>
    /// Splits raw stdout into (clean output, transcript, output parameters). See RunScriptActivity
    /// for the marker ordering contract. The captured <c>$LASTEXITCODE</c> always wins over a user
    /// variable named <c>exitCode</c>.
    /// </summary>
    public static (string cleanOutput, string? transcript, Dictionary<string, string> parameters)
        ExtractMarkers(string output, string stepId, ILogger logger)
    {
        var parameters = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(output))
            return (output, null, parameters);

        var work = StripMarkerLine(output, ErrorMarker);

        string? capturedExitCode = null;
        var exitIdx = work.LastIndexOf(ExitCodeMarker, StringComparison.Ordinal);
        if (exitIdx >= 0)
        {
            capturedExitCode = work[(exitIdx + ExitCodeMarker.Length)..].Trim();
            work = work[..exitIdx].TrimEnd();
        }

        var paramsIdx = work.LastIndexOf(ParamsMarker, StringComparison.Ordinal);
        if (paramsIdx >= 0)
        {
            var json = work[(paramsIdx + ParamsMarker.Length)..].Trim();
            work = work[..paramsIdx].TrimEnd();
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        parameters[prop.Name] = prop.Value.ToString();
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex,
                        "Failed to parse capture block for step {StepId} — output parameters unavailable. JSON length: {Length}",
                        stepId, json.Length);
                }
            }
        }

        if (!string.IsNullOrEmpty(capturedExitCode))
            parameters["exitCode"] = capturedExitCode;

        string? transcript = null;
        var startIdx = work.IndexOf(TranscriptStartMarker, StringComparison.Ordinal);
        var endIdx = work.LastIndexOf(TranscriptEndMarker, StringComparison.Ordinal);
        if (startIdx >= 0 && endIdx > startIdx)
        {
            var contentStart = startIdx + TranscriptStartMarker.Length;
            transcript = work[contentStart..endIdx].Trim('\r', '\n', ' ', '\t');

            var beforeTranscript = work[..startIdx].TrimEnd();
            var afterTranscript = work[(endIdx + TranscriptEndMarker.Length)..].TrimStart('\r', '\n', ' ', '\t');
            work = string.IsNullOrEmpty(afterTranscript)
                ? beforeTranscript
                : (beforeTranscript.Length == 0 ? afterTranscript : beforeTranscript + "\n" + afterTranscript);
        }

        return (work, transcript, parameters);
    }

    private static string StripMarkerLine(string text, string marker)
    {
        var idx = text.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return text;
        var before = text[..idx].TrimEnd('\r', '\n');
        var after = text[(idx + marker.Length)..].TrimStart('\r', '\n');
        return before.Length == 0 ? after : (after.Length == 0 ? before : before + "\n" + after);
    }

    /// <summary>
    /// Engine-agnostic finalisation of success + the <c>exitCode</c> param. Default (null
    /// <paramref name="successExitCodes"/>) is pure error-based; when set, success also requires the
    /// captured exit code to be in the set.
    /// </summary>
    public static (bool success, Dictionary<string, string> parameters) ApplyExitCodeSemantics(
        bool engineSuccess,
        Dictionary<string, string> parameters,
        int? fallbackExitCode,
        HashSet<int>? successExitCodes)
    {
        if (!parameters.ContainsKey("exitCode"))
            parameters["exitCode"] = (fallbackExitCode ?? 0).ToString();

        var success = engineSuccess;
        if (successExitCodes is not null
            && int.TryParse(parameters["exitCode"], out var ec)
            && !successExitCodes.Contains(ec))
            success = false;

        return (success, parameters);
    }

    /// <summary>Parses a comma-separated exit-code allow-list; null/empty → null (no gate).</summary>
    public static HashSet<int>? ParseSuccessExitCodes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var set = new HashSet<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var n)) set.Add(n);
        return set.Count == 0 ? null : set;
    }
}
