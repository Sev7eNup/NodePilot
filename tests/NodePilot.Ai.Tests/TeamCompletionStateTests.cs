using NodePilot.Ai.Agents;
using NodePilot.Core.Agents;
using Xunit;

namespace NodePilot.Ai.Tests;

public sealed class TeamCompletionStateTests
{
    [Fact]
    public void ProtocolFailureDoesNotClearAnExistingEvidenceObjection()
    {
        var state = Create();
        state.Record("review", "needs_input", "Read the effective configuration");
        state.Record("review", "failed", "Invalid response format", requiresNewEvidence: false);
        Assert.False(state.Record("review", "completed", "Corrected response envelope"));
        state.Observe("worker", "read", "configuration", "Original values");
        Assert.True(state.Record("review", "completed", "Configuration checked"));
    }

    [Fact]
    public void ScopedReviewSurvivesUnrelatedFindingButNotItsOwnChangedBasis()
    {
        var state = new TeamCompletionState([
            new() { Id = "client" }, new() { Id = "server" },
            new() { Id = "review", IsReviewer = true }]);
        state.Record("client", "completed", "Client identity verified");
        state.Record("server", "completed", "Server config verified");
        state.Record("review", "completed", "Client checked", dependencies: ["member:client"]);
        state.Record("server", "completed", "Additional server finding");
        Assert.Null(state.GetBlockers());
        state.Observe("client", "read", "identity", "changed identity");
        Assert.Contains("member:client", state.GetBlockers());
    }

    [Fact]
    public void ScopedReviewCannotApproveBasisChangedDuringAssignment()
    {
        var state = Create();
        var revision = state.Revision;
        state.Observe("worker", "read", "config", "changed");
        state.Record("review", "completed", "Old evidence", reviewedRevision: revision, dependencies: ["member:worker"]);
        state.Record("second", "completed", "Current");
        Assert.Contains("review", state.GetBlockers());
    }

    [Fact]
    public void CheckChangesInvalidateDependentReviewsAndGlobalChangesInvalidateAll()
    {
        var state = Create();
        state.InvalidateReviews("check:client");
        state.InvalidateReviews("check:server");
        state.Record("review", "completed", "Client", dependencies: ["check:client"]);
        state.Record("second", "completed", "Server", dependencies: ["check:server"]);
        state.InvalidateReviews("check:server");
        var blockers = System.Text.Json.JsonDocument.Parse(state.GetBlockers()!).RootElement;
        Assert.Equal(new[] { "second" }, blockers.GetProperty("reviewRequired").EnumerateArray().Select(x => x.GetString()!).ToArray());
        state.Record("second", "completed", "Rechecked", dependencies: ["check:server"]);
        Assert.Null(state.GetBlockers());
        state.InvalidateReviews();
        Assert.Equal(2, state.PendingCount);
    }

    [Theory]
    [InlineData("member:missing")]
    [InlineData("check:missing")]
    public void UnknownReviewDependencyCannotGrantApproval(string dependency)
    {
        var state = Create();
        Assert.Throws<ArgumentException>(() => state.Record("review", "completed", "Invalid", dependencies: [dependency]));
        Assert.NotNull(state.GetBlockers());
    }

    [Fact]
    public void BoardEvictionCountsOnlyUnreadPeerEntries()
    {
        var board = new TeamBoard();
        board.BeginAssignment("worker");
        for (var i = 0; i < 300; i++) board.Evidence("worker", "own-" + i, "read", null, "", "");
        Assert.Null(board.TakeDelta("worker", 4000));
        board.Evidence("peer", "peer-1", "read", null, "", "");
        for (var i = 0; i < 256; i++) board.Evidence("worker", "own-" + i, "read", null, "", "");
        var delta = Assert.IsType<TeamBoard.Delta>(board.TakeDelta("worker", 4000));
        Assert.Empty(delta.Ids);
        Assert.Equal(1, delta.Omitted);
        Assert.Null(board.TakeDelta("worker", 4000));
    }

    [Fact]
    public void BoardDeltaIsBoundedDeliveredOnceAndNeverCountsAsEvidence()
    {
        var state = Create();
        var board = new TeamBoard();
        board.BeginAssignment("worker");
        board.Evidence("worker", "own", "read", null, "query", "own evidence");
        for (var i = 0; i < 20; i++) board.Evidence("second", "ev-" + i, "read", null, "query", new string('x', 180));
        var delta = Assert.IsType<TeamBoard.Delta>(board.TakeDelta("worker", 1000));
        Assert.True(delta.Content.Length <= 1000);
        Assert.True(delta.Omitted > 0);
        Assert.Equal("ev-19", delta.Ids[0]);
        Assert.DoesNotContain("own", delta.Ids);
        Assert.Null(board.TakeDelta("worker", 1000));
        Assert.Equal(0, state.ObservationCount);
        state.Record("review", "needs_input", "Need original");
        Assert.False(state.Record("review", "completed", "Board mentions it"));
        board.BeginAssignment("review");
        Assert.Null(board.TakeDelta("review", 1000));
    }

    [Fact]
    public async Task ConcurrentObservationsAreRetainedAndStaleReviewCannotApproveNewRevision()
    {
        var state = Create();
        var revision = state.Revision;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() => {
            state.Observe("worker", "read", i.ToString(), "value");
            state.GetMemberFindings("review");
            state.GetBlockers();
        }, TestContext.Current.CancellationToken)));
        Assert.Equal(100, state.ObservationCount);
        state.Record("review", "completed", "Old snapshot", reviewedRevision: revision);
        state.Record("second", "completed", "Current");
        Assert.Contains("review", state.GetBlockers());
        state.Record("review", "completed", "Current", reviewedRevision: state.Revision);
        Assert.Null(state.GetBlockers());
    }
    [Fact]
    public void ProcessEnvelopeClockAndElapsedTimeAreNotNewEvidence()
    {
        var state = Create();
        state.Observe("worker", "powershell", "Get-Service", "{\"stdout\":\"Stopped\",\"exitCode\":0,\"timeContext\":{\"observedAtUtc\":\"first\"},\"durationMs\":10}");
        state.Record("review", "needs_input", "Inspect the component");
        state.Observe("worker", "powershell", "Get-Service", "{\"stdout\":\"Stopped\",\"exitCode\":0,\"timeContext\":{\"observedAtUtc\":\"second\"},\"durationMs\":20}");
        Assert.Equal(1, state.ObservationCount);
        Assert.False(state.Record("review", "completed", "Same values, later envelope"));
    }

    [Fact]
    public void ReviewRepetitionRequiresNewObservationRatherThanRegisterEdits()
    {
        var state = Create();
        Assert.True(state.TryBeginReview("review"));
        Assert.True(state.TryBeginReview("review"));
        Assert.True(state.TryBeginReview("review"));
        state.InvalidateReviews();
        Assert.False(state.TryBeginReview("review"));
        Assert.True(state.TryBeginReview("worker"));
        state.Observe("worker", "read", "configuration", "new observation");
        Assert.True(state.TryBeginReview("review"));
    }

    [Fact]
    public void RepeatedUnchangedFindingKeepsReviewsButNewObservationInvalidatesThem()
    {
        var state = Create();
        state.Record("worker", "completed", "Observed value, ev-00001");
        state.Record("review", "completed", "Checked");
        state.Record("second", "completed", "Checked");
        state.Record("worker", "completed", "Observed value, ev-00001");
        Assert.Null(state.GetBlockers());
        state.Observe("worker", "read", "counterpart", "new value");
        state.Record("worker", "completed", "Observed value, ev-00001");
        Assert.Contains("review", state.GetBlockers());
    }

    [Fact]
    public void TextRevisionRequiresNewReviewButNotNewToolEvidence()
    {
        var state = new TeamCompletionState([new() { Id = "review", IsReviewer = true }]);
        state.Record("review", "needs_input", "Add a proposed verification step", requiresNewEvidence: false);
        Assert.NotNull(state.GetBlockers());
        Assert.True(state.Record("review", "completed", "The revised proposal addresses the objection"));
        Assert.Null(state.GetBlockers());
    }

    [Fact]
    public void TextRevisionCannotClearAnEarlierUnresolvedEvidenceRequirement()
    {
        var state = new TeamCompletionState([new() { Id = "review", IsReviewer = true }]);
        state.Record("review", "needs_input", "Read configuration");
        state.Record("review", "needs_input", "Also correct the wording", requiresNewEvidence: false);
        Assert.False(state.Record("review", "completed", "Wording corrected"));
        Assert.NotNull(state.GetBlockers());
    }
    [Fact]
    public void AnotherReviewersNewObservationCanSupportTheOwnersSubsequentApproval()
    {
        var state = Create();
        state.Record("review", "needs_input", "Verify endpoint");
        state.Observe("second", "read", "server", "other review");
        state.Record("second", "completed", "Server checked");
        Assert.Contains("Verify endpoint", state.GetBlockers());
        Assert.True(state.Record("review", "completed", "Reviewed the other member's original endpoint evidence"));
        Assert.Null(state.GetBlockers());
    }

    [Fact]
    public void RepeatingTheSameObservationThroughAnotherRoleDoesNotCreateNewEvidence()
    {
        var state = Create();
        state.Observe("worker", "read", "endpoint", "old result");
        state.Record("review", "needs_input", "Find additional evidence");
        state.Observe("second", "read", "endpoint", "old result");
        Assert.False(state.Record("review", "completed", "Same observation from another member"));
    }

    [Fact]
    public void EquivalentValuesOnDifferentTargetsAreDistinctObservations()
    {
        var state = new TeamCompletionState([
            new() { Id = "client", IsReviewer = true, TargetMachineId = Guid.NewGuid() },
            new() { Id = "server", IsReviewer = true, TargetMachineId = Guid.NewGuid() }]);
        state.Observe("server", "read", "configuration", "value");
        state.Record("server", "needs_input", "Compare the client counterpart");
        state.Observe("client", "read", "configuration", "value");
        state.Record("client", "completed", "Client counterpart checked");
        Assert.True(state.Record("server", "completed", "Compared both original observations"));
        Assert.Null(state.GetBlockers());
    }
    [Fact]
    public void RenewedObjectionRequiresEvidenceAfterThatObjection()
    {
        var state = Create();
        state.Record("review", "needs_input", "Read endpoint");
        state.Observe("worker", "read", "endpoint", "value");
        state.Record("review", "needs_input", "Now verify policy scope");
        Assert.False(state.Record("review", "completed", "Done"));
        Assert.Contains("Now verify policy scope", state.GetBlockers());
    }
    [Fact]
    public void RepeatedObservationCannotClearANewObjection()
    {
        var state = Create();
        state.Observe("worker", "read", "endpoint", "same evidence");
        state.Record("review", "needs_input", "Check actual configuration");
        state.Observe("worker", "read", "endpoint", "same evidence");
        Assert.False(state.Record("review", "completed", "Done"));
        state.Observe("worker", "read", "configuration", "new evidence");
        Assert.True(state.Record("review", "completed", "Verified"));
        state.Record("second", "completed", "Verified");
        Assert.Null(state.GetBlockers());
    }
    [Fact]
    public void ReviewerCannotWithdrawEvidenceObjectionWithoutNewObservation()
    {
        var state = Create();
        state.Record("review", "needs_input", "Verify the endpoint");
        state.Record("review", "completed", "Supervisor says close with limitations");
        state.Record("second", "completed", "Checked");
        Assert.Contains("Verify the endpoint", state.GetBlockers());
    }
    private static TeamCompletionState Create() => new([
        new AgentDefinition { Id = "worker" },
        new AgentDefinition { Id = "review", IsReviewer = true },
        new AgentDefinition { Id = "second", IsReviewer = true }
    ]);

    [Fact]
    public void OtherMembersCannotClearAnOpenReviewQuestion()
    {
        var state = Create();
        state.Record("review", "needs_input", "Verify the endpoint");
        state.Record("worker", "completed", "Endpoint verified");
        state.Record("second", "completed", "Checked");
        Assert.Contains("Verify the endpoint", state.GetBlockers());
        state.Observe("worker", "read", "endpoint", "verified");
        state.Record("review", "completed", "Checked the endpoint evidence");
        Assert.Contains("second", state.GetBlockers());
        state.Record("second", "completed", "Checked the new endpoint evidence too");
        Assert.Null(state.GetBlockers());
    }

    [Fact]
    public void SupervisorEvidenceInvalidatesEveryReview()
    {
        var state = Create();
        state.Record("review", "completed", "Checked");
        state.Record("second", "completed", "Checked");
        Assert.Null(state.GetBlockers());
        state.InvalidateReviews();
        state.Record("review", "completed", "Checked new evidence");
        Assert.Contains("second", state.GetBlockers());
        state.Record("second", "completed", "Checked new evidence");
        Assert.Null(state.GetBlockers());
    }

    [Fact]
    public void FailureDoesNotClearAQuestionAndRequiresNewReviewAfterRecovery()
    {
        var state = Create();
        state.Record("worker", "failed", "Missing evidence");
        state.Record("review", "completed", "Limited evidence");
        state.Record("second", "completed", "Limited evidence");
        Assert.Contains("Missing evidence", state.GetBlockers());
        state.Record("worker", "completed", "Evidence obtained");
        Assert.DoesNotContain("Missing evidence", state.GetBlockers());
        Assert.Contains("reviewRequired", state.GetBlockers());
    }
}
