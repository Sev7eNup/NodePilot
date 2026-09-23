using NodePilot.Ai.Agents;
using Xunit;

namespace NodePilot.Ai.Tests;

public sealed class DelegationResponseTests
{
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
