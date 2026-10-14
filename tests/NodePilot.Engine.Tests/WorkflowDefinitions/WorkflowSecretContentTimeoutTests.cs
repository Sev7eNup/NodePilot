using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Engine.Tests.WorkflowDefinitions;

/// <summary>
/// The regex helpers behind secret detection must fail closed: a timeout in any step (template
/// stripping, line matching, pattern matching) has to count as "looks secret".
/// </summary>
public sealed class WorkflowSecretContentTimeoutTests
{
    // Catastrophic backtracking with a 1 ms budget times out reliably.
    private static readonly Regex Pathological = new("(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1));
    private static readonly string Input = new string('a', 64) + "!";

    [Fact]
    public void SafeReplace_Timeout_ReturnsNull()
        => WorkflowSecretContent.SafeReplace(Pathological, Input, " ").Should().BeNull();

    [Fact]
    public void SafeMatch_Timeout_ReturnsNull()
        => WorkflowSecretContent.SafeMatch(Pathological, Input).Should().BeNull();

    [Fact]
    public void SafeIsMatch_Timeout_ReturnsTrue()
        => WorkflowSecretContent.SafeIsMatch(Pathological, Input).Should().BeTrue();

    [Fact]
    public void SafeReplace_NoTimeout_ReturnsReplacedText()
        => WorkflowSecretContent.SafeReplace(new Regex("b"), "abc", "x").Should().Be("axc");
}
