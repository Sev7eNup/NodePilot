using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Engine.Execution;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

/// <summary>
/// The <c>{{step.TAIL}}</c> grammar lives in three places: the engine runtime
/// (<see cref="VariableResolver.StepPattern"/>), the MCP static analyzer
/// (<c>NodePilot.Mcp/Analysis/VariableResolver.cs</c>), and the frontend databus helpers.
/// FE↔MCP parity is guarded by <c>WorkflowAnalyzerFrontendParityTests</c>; nothing guarded
/// ENGINE↔MCP — if the engine gains a tail, the MCP linter starts reporting false
/// "won't resolve" errors for workflows that run fine (coherence audit 2026-08). This test
/// derives the authoritative tail set from the engine's actual compiled pattern and asserts
/// the MCP analyzer's source validates exactly that set.
/// </summary>
public sealed class TemplateGrammarParityTests
{
    [Fact]
    public void McpAnalyzer_ValidatesExactlyTheEngineTailGrammar()
    {
        // Authoritative side: parse the alternation out of the engine's real pattern —
        // not a copy of it — so an engine grammar change flips this test by itself.
        var pattern = VariableResolver.StepPattern.ToString();
        var alternation = Regex.Match(pattern, @"\\\.\((?<body>.*)\)\\\}\\\}");
        alternation.Success.Should().BeTrue(
            $"the engine StepPattern no longer has the expected '\\.(…)}}}}' shape — update this parser. Pattern: {pattern}");

        var body = alternation.Groups["body"].Value;
        var simpleTails = body.Split('|')
            .Where(t => Regex.IsMatch(t, "^[a-zA-Z]+$"))
            .ToList();
        simpleTails.Should().NotBeEmpty("the engine grammar must declare simple tails");
        body.Should().Contain(@"param\.", "the engine grammar must keep the param.X tail");

        // Mirror side: the MCP analyzer's validTail expression, read as text.
        var mcpSource = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "NodePilot.Mcp", "Analysis", "VariableResolver.cs"));
        var validTailLine = Regex.Match(mcpSource, @"validTail\s*=\s*(?<expr>[^;]+);");
        validTailLine.Success.Should().BeTrue(
            "expected the MCP analyzer to keep its tail check in a 'validTail = …;' expression — if it moved, update this scan");

        var expr = validTailLine.Groups["expr"].Value;
        var mcpTails = Regex.Matches(expr, @"""(?<t>[a-zA-Z]+)""")
            .Select(m => m.Groups["t"].Value)
            .Where(t => t != "param") // the param tail is asserted separately as a prefix check
            .ToList();

        mcpTails.Should().BeEquivalentTo(simpleTails,
            "the MCP analyzer must accept exactly the tails the engine resolves — " +
            "a missing tail produces false lint errors, an extra one hides real ones");
        expr.Should().Contain("param.",
            "the MCP analyzer must keep accepting the param.X prefix tail");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && dir is not null; depth++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }
}
