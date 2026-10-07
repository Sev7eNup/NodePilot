using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Management.Automation.Language;
using Microsoft.Extensions.Logging;
using NodePilot.Engine.Execution;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.PowerShell;

/// <summary>
/// Pure, side-effect-free primitives shared by <c>RunScriptActivity</c> and
/// <c>CustomActivityExecutor</c> (a custom activity is a reusable runScript preset):
/// PowerShell-safe
/// <c>{{...}}</c> resolution into the script text, the marker-block parser that splits raw stdout
/// into clean output / transcript / output-parameters / exit code, and the error-based success
/// rule.
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
    /// Resolves <c>{{globals.NAME}}</c>, <c>{{manual.NAME}}</c> and
    /// <c>{{varName.output|error|success|param.x}}</c> in script text. In code context textual
    /// values become single-quoted PowerShell literals. String content is escaped for its
    /// parser context; literal here-strings are re-emitted without altering their value. Booleans become
    /// <c>$true</c>/<c>$false</c>. Unresolved references are left verbatim.
    /// </summary>
    public static string ResolveScriptVariables(string script, Dictionary<string, string> variables)
    {
        if (!script.Contains("{{"))
            return script;

        // Use PowerShell's own parser to find lexical context instead of a hand-written quote
        // scanner, which cannot tell a comment's apostrophe from a string delimiter. Replace
        // matches right to left so offsets stay valid; fail closed on any ambiguous extent.
        var expressions = FindTemplateExpressions(script);
        if (expressions.Count == 0)
            return script;

        var contexts = AnalyzeTemplateContexts(script, expressions, out var coveringTokens, out var nestedStrings, out var wordEnds);
        var resolved = new StringBuilder(script);
        for (var i = expressions.Count - 1; i >= 0; i--)
        {
            var expression = expressions[i];
            var context = contexts[i];

            // Templates in comments have no runtime meaning. Leaving them verbatim also
            // prevents a value containing a newline or comment terminator from becoming code.
            if (context == TemplateContext.Comment)
                continue;

            if (context == TemplateContext.WordStart)
            {
                i = ResolveWord(script, expressions, contexts, wordEnds, variables, i, resolved);
                continue;
            }

            if (context == TemplateContext.SingleQuotedString && nestedStrings[i])
            {
                var token = coveringTokens[i];
                var literalSource = script.Substring(token.Extent.StartOffset, token.Text.Length);
                _ = Parser.ParseInput(literalSource, out var literalTokens, out _);
                var literal = new StringBuilder(((StringToken)literalTokens[0]).Value);
                var valueExpressions = FindTemplateExpressions(literal.ToString());
                var changed = false;
                foreach (var valueExpression in valueExpressions.AsEnumerable().Reverse())
                {
                    var value = ResolveTemplateExpression(valueExpression, variables, TemplateContext.SingleQuotedHereString);
                    if (value is null) continue;
                    literal.Remove(valueExpression.Index, valueExpression.Length);
                    literal.Insert(valueExpression.Index, value);
                    changed = true;
                }
                if (changed)
                {
                    resolved.Remove(token.Extent.StartOffset, token.Text.Length);
                    resolved.Insert(token.Extent.StartOffset, $"({FormatEncodedExpression(literal.ToString())})");
                }
                while (i > 0 && ReferenceEquals(coveringTokens[i - 1], token)) i--;
                continue;
            }

            if (context == TemplateContext.SingleQuotedHereString)
            {
                var token = (StringToken)coveringTokens[i];
                var content = new StringBuilder(token.Value);
                var headerLength = token.Text.IndexOfAny(['\r', '\n']) + 1;
                if (token.Text[headerLength - 1] == '\r' && token.Text[headerLength] == '\n')
                    headerLength++;
                var changed = false;
                // Re-emit the entire literal: here-string terminators cannot be escaped in
                // place without changing the data. Parser offsets preserve surrounding text.
                do
                {
                    expression = expressions[i];
                    var value = ResolveTemplateExpression(expression, variables, context);
                    changed |= value is not null;
                    var offset = expression.Index - token.Extent.StartOffset - headerLength;
                    content.Remove(offset, expression.Length);
                    content.Insert(offset, value ?? expression.Match.Value);
                    i--;
                } while (i >= 0 && ReferenceEquals(coveringTokens[i], token));
                i++;

                if (changed)
                {
                    resolved.Remove(token.Extent.StartOffset, token.Text.Length);
                    resolved.Insert(token.Extent.StartOffset, nestedStrings[i]
                        ? $"({FormatEncodedExpression(content.ToString())})"
                        : PowerShellQuoter.Literal(content.ToString()));
                }
                continue;
            }

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
        // A template that starts a bareword (`{{x}}\app.txt`), or any template inside such a word.
        WordStart,
    }

    /// <summary>
    /// Rewrites a bareword that starts with a template as one double-quoted word, each template as
    /// an encoded subexpression: `"$(…)\app.txt"`. A quoted value at the start of a word would end
    /// that argument and turn the rest of the word into a second one. Returns the index of the
    /// word's first template.
    /// </summary>
    private static int ResolveWord(
        string script,
        IReadOnlyList<TemplateExpression> expressions,
        TemplateContext[] contexts,
        int[] wordEnds,
        IReadOnlyDictionary<string, string> variables,
        int last,
        StringBuilder resolved)
    {
        var wordEnd = wordEnds[last];
        var first = last;
        while (first > 0 && contexts[first - 1] == TemplateContext.WordStart && wordEnds[first - 1] == wordEnd)
            first--;

        var wordStart = expressions[first].Index;
        var word = new StringBuilder("\"");
        var cursor = wordStart;
        var changed = false;
        for (var k = first; k <= last; k++)
        {
            var expression = expressions[k];
            word.Append(script, cursor, expression.Index - cursor);
            var value = ResolveTemplateExpression(expression, variables, TemplateContext.ExpandableStringSubexpression);
            if (value is null)
            {
                word.Append(expression.Match.Value);
            }
            else
            {
                word.Append("$(").Append(value).Append(')');
                changed = true;
            }
            cursor = expression.Index + expression.Length;
        }
        word.Append(script, cursor, wordEnd - cursor).Append('"');

        if (changed)
        {
            resolved.Remove(wordStart, wordEnd - wordStart);
            resolved.Insert(wordStart, word.ToString());
        }
        return first;
    }

    private enum TemplateKind { Global, Manual, Step }

    private sealed record TemplateExpression(Match Match, TemplateKind Kind)
    {
        public int Index => Match.Index;
        public int Length => Match.Length;

        /// <summary>Flat-namespace forms win an overlap against the step form — see
        /// FindTemplateExpressions.</summary>
        public bool IsFlatNamespace => Kind != TemplateKind.Step;
    }

    /// <summary>
    /// Collects every template extent in the script from both patterns, ordered by position.
    ///
    /// <para>The two patterns can overlap: a global may legitimately be named <c>output</c>,
    /// <c>error</c> or <c>success</c>, so <c>{{globals.output}}</c> matches both GlobalsPattern
    /// and StepPattern over the same span. Overlaps are dropped here, with the global
    /// interpretation winning, the same precedence <see cref="VariableResolver"/> applies.</para>
    /// </summary>
    private static List<TemplateExpression> FindTemplateExpressions(string script)
    {
        var expressions = new List<TemplateExpression>();
        expressions.AddRange(VariableResolver.GlobalsPattern.Matches(script)
            .Cast<Match>()
            .Select(match => new TemplateExpression(match, TemplateKind.Global)));
        expressions.AddRange(VariableResolver.ManualPattern.Matches(script)
            .Cast<Match>()
            .Select(match => new TemplateExpression(match, TemplateKind.Manual)));
        expressions.AddRange(VariableResolver.StepPattern.Matches(script)
            .Cast<Match>()
            .Select(match => new TemplateExpression(match, TemplateKind.Step)));

        // Sort by position; at equal position the flat-namespace form wins and is kept below.
        // A trigger input named "output"/"error"/"success" collides with StepPattern the same
        // way a global does, and matching it twice would shred the surrounding script.
        expressions.Sort(static (left, right) => left.Index != right.Index
            ? left.Index.CompareTo(right.Index)
            : right.IsFlatNamespace.CompareTo(left.IsFlatNamespace));

        var deduped = new List<TemplateExpression>(expressions.Count);
        var nextFreeIndex = 0;
        foreach (var expression in expressions)
        {
            if (expression.Index < nextFreeIndex) continue;
            deduped.Add(expression);
            nextFreeIndex = expression.Index + expression.Length;
        }

        return deduped;
    }

    /// <summary>
    /// Finds the parser context of every template in two passes over same-length surrogates, so
    /// every source offset stays valid. Pass 1 fills each template with identifier characters,
    /// which cannot open or close a string or comment, and classifies it. Pass 2 fills the code
    /// templates with a quoted literal, which is what the real substitution inserts there, and
    /// judges parse errors on that text: a bare word after a cast (<c>[int]xxxx</c>) is a parse
    /// error, the substituted <c>[int]'50'</c> is not.
    /// </summary>
    private static TemplateContext[] AnalyzeTemplateContexts(
        string script,
        IReadOnlyList<TemplateExpression> expressions,
        out Token[] coveringTokens,
        out bool[] nestedStrings,
        out int[] wordEnds)
    {
        _ = Parser.ParseInput(BuildSurrogate(script, expressions, quoted: null), out var tokens, out _);
        var contexts = new TemplateContext[expressions.Count];
        coveringTokens = new Token[expressions.Count];
        nestedStrings = new bool[expressions.Count];
        var flattenedTokens = FlattenTokens(tokens).ToArray();

        for (var i = 0; i < expressions.Count; i++)
        {
            var expression = expressions[i];
            var end = expression.Index + expression.Length;

            // Prefer the narrowest covering token. Expandable strings can contain nested
            // subexpression tokens; a template in `$()` is code, while a direct template in
            // the surrounding string is string content.
            var token = NarrowestCoveringToken(flattenedTokens, expression.Index, end);

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
                TokenKind.StringLiteral when token.Text[0] is '\'' or '\u2018' or '\u2019' or '\u201a' or '\u201b'
                    => TemplateContext.SingleQuotedString,
                TokenKind.StringExpandable when token.Text[0] is '"' or '\u201c' or '\u201d' or '\u201e'
                    => TemplateContext.DoubleQuotedString,
                _ => TemplateContext.Code,
            };

            // Inside `$()`, single-quoting arbitrary content is not safe since a `)` in the
            // value can terminate PowerShell's nested parse early. Encode the value as base64
            // instead, so an attacker controls only the base64 literal, never PowerShell syntax.
            nestedStrings[i] = flattenedTokens.Any(candidate =>
                    !ReferenceEquals(candidate, token)
                    && candidate.Extent.StartOffset <= expression.Index
                    && candidate.Extent.EndOffset >= end
                    && candidate.Kind is TokenKind.StringExpandable or TokenKind.HereStringExpandable);
            if (context == TemplateContext.Code && nestedStrings[i])
            {
                context = TemplateContext.ExpandableStringSubexpression;
            }

            contexts[i] = context;
            coveringTokens[i] = token;
        }

        var quoted = contexts
            .Select(context => context is TemplateContext.Code or TemplateContext.ExpandableStringSubexpression)
            .ToArray();
        var substitutedAst = Parser.ParseInput(BuildSurrogate(script, expressions, quoted), out var substitutedTokens, out var parseErrors);
        var flattenedSubstituted = FlattenTokens(substitutedTokens).ToArray();
        wordEnds = MarkWordStarts(script, expressions, contexts, coveringTokens, substitutedAst);

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

            // A word that starts with a template is re-emitted as a whole (ResolveWord).
            if (contexts[i] == TemplateContext.WordStart)
                continue;

            // The substitution must not change how the script around the template parses: a
            // quoted template stays exactly one string literal or a quoted part of its bareword,
            // any other keeps its pass-1 token.
            var token = NarrowestCoveringToken(flattenedSubstituted, expression.Index, end);
            var unchanged = quoted[i]
                ? (token is { Kind: TokenKind.StringLiteral }
                   && token.Extent.StartOffset == expression.Index && token.Extent.EndOffset == end)
                  || (contexts[i] == TemplateContext.Code && IsPlainBarewordPart(coveringTokens[i], token))
                : token is not null
                  && token.Kind == coveringTokens[i].Kind
                  && token.Extent.StartOffset == coveringTokens[i].Extent.StartOffset
                  && token.Extent.EndOffset == coveringTokens[i].Extent.EndOffset;
            if (!unchanged)
            {
                throw new InvalidOperationException(
                    $"PowerShell template at offset {expression.Index} has an unsafe or ambiguous syntax context: " +
                    "the substituted value would change how the surrounding script parses.");
            }
        }

        return contexts;
    }

    /// <summary>
    /// Marks templates that start a bareword the quoted value would split: in the parse with
    /// quoted templates the value stands as its own command argument, although without quotes
    /// the word runs on (`{{x}}\app.txt`). A member access such as `{{x}}.Trim()` is left alone.
    /// Every template inside such a word joins it. Returns each marked template's word end.
    /// </summary>
    private static int[] MarkWordStarts(
        string script,
        IReadOnlyList<TemplateExpression> expressions,
        TemplateContext[] contexts,
        Token[] coveringTokens,
        Ast substitutedAst)
    {
        var wordEnds = new int[expressions.Count];
        var literals = substitutedAst.FindAll(ast => ast is StringConstantExpressionAst, searchNestedScriptBlocks: true).ToArray();

        for (var i = 0; i < expressions.Count; i++)
        {
            if (contexts[i] != TemplateContext.Code) continue;
            var expression = expressions[i];
            var end = expression.Index + expression.Length;
            var word = coveringTokens[i];
            if (word.Kind != TokenKind.Generic
                || word.Extent.StartOffset != expression.Index
                || word.Extent.EndOffset <= end)
                continue;

            var literal = literals.FirstOrDefault(ast => ast.Extent.StartOffset == expression.Index && ast.Extent.EndOffset == end);
            if (!SplitsItsArgument(literal))
                continue;

            var wordEnd = word.Extent.EndOffset;
            var members = Enumerable.Range(0, expressions.Count)
                .Where(j => expressions[j].Index >= expression.Index && expressions[j].Index < wordEnd)
                .ToArray();

            // The word becomes one double-quoted string; its own quotes would change meaning there.
            var cursor = expression.Index;
            foreach (var j in members)
            {
                if (HasQuote(script, cursor, expressions[j].Index))
                    throw new InvalidOperationException(
                        $"PowerShell template at offset {expression.Index} has an unsafe or ambiguous syntax context: " +
                        "the word it starts also contains quotes. Assign the value to a variable first.");
                cursor = expressions[j].Index + expressions[j].Length;
            }
            if (HasQuote(script, cursor, wordEnd))
                throw new InvalidOperationException(
                    $"PowerShell template at offset {expression.Index} has an unsafe or ambiguous syntax context: " +
                    "the word it starts also contains quotes. Assign the value to a variable first.");

            foreach (var j in members)
            {
                contexts[j] = TemplateContext.WordStart;
                wordEnds[j] = wordEnd;
            }
        }
        return wordEnds;
    }

    /// <summary>
    /// True when the quoted value is a command argument of its own (`'x'\app.txt` is two
    /// arguments) or the target of a bare member name in argument position (`'x'.txt` reads a
    /// property `txt`). A method call such as `'x'.ToUpper()` is meant as one.
    /// </summary>
    private static bool SplitsItsArgument(Ast? literal)
        => literal?.Parent switch
        {
            CommandAst or CommandParameterAst => true,
            MemberExpressionAst member and not InvokeMemberExpressionAst
                => ReferenceEquals(member.Expression, literal)
                   && member.Member is StringConstantExpressionAst
                   && member.Parent is CommandAst or CommandParameterAst,
            _ => false,
        };

    private static bool HasQuote(string script, int start, int end)
        => script.IndexOfAny(QuoteCharacters, start, end - start) >= 0;

    private static readonly char[] QuoteCharacters =
        ['\'', '"', '‘', '’', '‚', '‛', '“', '”', '„'];

    /// <summary>
    /// True when a template sits inside a bareword such as <c>C:\t\{{x}}.txt</c> or
    /// <c>$dir\f-{{x}}.txt</c> and the quoted value becomes one quoted part of that same word.
    /// The word must hold no quote, backtick or <c>$(</c> of its own, so the template cannot sit
    /// inside an existing quoted part or subexpression. A variable such as <c>$dir</c> is fine: a
    /// template glued to it would make the variable token, not the word, the covering token.
    /// </summary>
    private static bool IsPlainBarewordPart(Token original, Token? substituted)
        => original is { Kind: TokenKind.Generic }
           && substituted is { Kind: TokenKind.Generic }
           && substituted.Extent.StartOffset == original.Extent.StartOffset
           && substituted.Extent.EndOffset == original.Extent.EndOffset
           && original.Text.IndexOfAny(BarewordSpecialCharacters) < 0
           && !original.Text.Contains("$(", StringComparison.Ordinal);

    private static readonly char[] BarewordSpecialCharacters =
        ['\'', '"', '`', '\u2018', '\u2019', '\u201a', '\u201b', '\u201c', '\u201d', '\u201e'];

    /// <summary>
    /// The script with every template replaced by a same-length run of <c>x</c>, or, where
    /// <paramref name="quoted"/> is set, by a same-length single-quoted literal.
    /// </summary>
    private static string BuildSurrogate(string script, IReadOnlyList<TemplateExpression> expressions, bool[]? quoted)
    {
        var surrogate = script.ToCharArray();
        for (var i = 0; i < expressions.Count; i++)
        {
            var expression = expressions[i];
            Array.Fill(surrogate, 'x', expression.Index, expression.Length);
            if (quoted?[i] == true)
            {
                surrogate[expression.Index] = '\'';
                surrogate[expression.Index + expression.Length - 1] = '\'';
            }
        }
        return new string(surrogate);
    }

    private static Token? NarrowestCoveringToken(Token[] tokens, int start, int end)
        => tokens
            .Where(candidate => candidate.Extent.StartOffset <= start && candidate.Extent.EndOffset >= end)
            .OrderBy(candidate => candidate.Extent.EndOffset - candidate.Extent.StartOffset)
            .FirstOrDefault();

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
        if (expression.Kind is TemplateKind.Global or TemplateKind.Manual)
        {
            var prefix = expression.Kind == TemplateKind.Global ? "globals" : "manual";
            var key = $"{prefix}.{match.Groups[1].Value}";
            return variables.TryGetValue(key, out var flatValue)
                ? FormatResolvedValue(flatValue, context)
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
            TemplateContext.SingleQuotedHereString => value,
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
        => PowerShellQuoter.EscapeSingleQuotedContent(value);

    private static string EscapeDoubleQuotedContent(string value)
        => value
            .Replace("`", "``")
            .Replace("$", "`$")
            .Replace("\"", "`\"")
            .Replace("\u201c", "`\u201c")
            .Replace("\u201d", "`\u201d")
            .Replace("\u201e", "`\u201e");

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

        text = VariableResolver.ManualPattern.Replace(text, m =>
        {
            var key = $"manual.{m.Groups[1].Value}";
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
        work = StripMarkerLine(work, PowerShellScriptWrapper.StartMarker);

        // A marker's value is the first following line of the expected shape, not everything
        // after it: a program the script started without a new window can write into the same
        // stream, before, between or after the wrapper's lines. Its lines stay ordinary output.
        var lines = new List<string>(work.Split('\n'));
        var capturedExitCode = TakeMarkerValue(lines, ExitCodeMarker, IsExitCodeLine);
        var json = TakeMarkerValue(lines, ParamsMarker, line => line.StartsWith('{'));
        work = string.Join('\n', lines).TrimEnd();

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

    /// <summary>
    /// Removes the last line equal to <paramref name="marker"/> and the first line after it that
    /// satisfies <paramref name="isValue"/>, and returns that value. Null when either is missing.
    /// </summary>
    private static string? TakeMarkerValue(List<string> lines, string marker, Func<string, bool> isValue)
    {
        var markerIdx = lines.FindLastIndex(line => line.Trim() == marker);
        if (markerIdx < 0) return null;
        lines.RemoveAt(markerIdx);
        for (var i = markerIdx; i < lines.Count; i++)
        {
            var candidate = lines[i].Trim();
            if (!isValue(candidate)) continue;
            lines.RemoveAt(i);
            return candidate;
        }
        return null;
    }

    private static bool IsExitCodeLine(string line)
        => int.TryParse(line, System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out _);

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
    /// <paramref name="successExitCodes"/>) is pure error-based; when set, success also requires
    /// the
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

    /// <summary>Parses a comma-separated exit-code allow-list; null/empty to null (no
    /// gate).</summary>
    public static HashSet<int>? ParseSuccessExitCodes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var set = new HashSet<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var n)) set.Add(n);
        return set.Count == 0 ? null : set;
    }
}
