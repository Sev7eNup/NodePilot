using NodePilot.Ai.Agents;
using Xunit;

namespace NodePilot.Ai.Tests;

public sealed class DelegationResponseTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("[42]")]
    [InlineData("[\"\"]")]
    [InlineData("null")]
    public void InvalidDependenciesCannotBeSilentlyTreatedAsApproval(string dependencies)
    {
        Assert.Throws<System.Text.Json.JsonException>(() => DelegationResponse.Parse(
            "{\"status\":\"completed\",\"content\":\"Checked\",\"verdict\":\"approved\",\"openChecks\":[],\"reviewDependencies\":" + dependencies + "}", true));
    }

    [Fact]
    public void NeedsInputPreservesStructuredChecksBeforeLongNarrative()
    {
        var response = DelegationResponse.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            status = "needs_input", verdict = "needs_work",
            openChecks = new[] { "Read effective endpoint configuration" },
            content = new string('x', 3000)
        }), true);
        var state = new TeamCompletionState([new() { Id = "review", IsReviewer = true }]);
        state.Record("review", response.Status, response.Content);
        Assert.Contains("Read effective endpoint configuration", state.GetBlockers());
    }

    [Fact]
    public void MalformedNeedsInputChecksAreNotSilentlyDiscarded()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => DelegationResponse.Parse(
            """{"status":"needs_input","content":"Evidence incomplete","openChecks":[42]}""", true));
    }
}
