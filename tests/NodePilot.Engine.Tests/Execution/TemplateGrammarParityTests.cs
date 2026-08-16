using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Engine.Execution;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

/// <summary>
/// The <c>{{step.TAIL}}</c> grammar lives in three places: the engine runtime
/// (<see cref="VariableResolver.StepPattern"/>), the static databus analyzer
/// (<c>NodePilot.Core/WorkflowDefinitions/WorkflowDataBusAnalyzer.cs</c>, shared by the MCP
/// tool and the AI chat), and the frontend databus helpers. FE↔analyzer parity is guarded by
/// <c>WorkflowAnalyzerFrontendParityTests</c>; nothing guarded ENGINE↔analyzer — if the engine
/// gains a tail, the linter starts reporting false "won't resolve" errors for workflows that
/// run fine (coherence audit 2026-08). This test derives the authoritative tail set from the
/// engine's actual compiled pattern and asserts the analyzer's source validates exactly that
/// set.
///
/// <para>The analyzer is located by PATH, so a move breaks this test rather than silently
/// disabling it — which is what happened when the analyzer was consolidated into Core. Keep
/// the path in step with the file.</para>
/// </summary>
public sealed class TemplateGrammarParityTests
{
    [Fact]
    public void DataBusAnalyzer_ValidatesExactlyTheEngineTailGrammar()
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

        // Mirror side: the analyzer's validTail expression, read as text.
        var analyzerPath = Path.Combine(
            RepoRoot(), "src", "NodePilot.Core", "WorkflowDefinitions", "WorkflowDataBusAnalyzer.cs");
        File.Exists(analyzerPath).Should().BeTrue(
            $"the databus analyzer is located by path; it moved away from {analyzerPath} and this guard must follow it");
        var mcpSource = File.ReadAllText(analyzerPath);
        var validTailLine = Regex.Match(mcpSource, @"validTail\s*=\s*(?<expr>[^;]+);");
        validTailLine.Success.Should().BeTrue(
            "expected the analyzer to keep its tail check in a 'validTail = …;' expression — if it moved, update this scan");

        var expr = validTailLine.Groups["expr"].Value;
        var mcpTails = Regex.Matches(expr, @"""(?<t>[a-zA-Z]+)""")
            .Select(m => m.Groups["t"].Value)
            .Where(t => t != "param") // the param tail is asserted separately as a prefix check
            .ToList();

        mcpTails.Should().BeEquivalentTo(simpleTails,
            "the analyzer must accept exactly the tails the engine resolves — " +
            "a missing tail produces false lint errors, an extra one hides real ones");
        expr.Should().Contain("param.",
            "the analyzer must keep accepting the param.X prefix tail");
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
