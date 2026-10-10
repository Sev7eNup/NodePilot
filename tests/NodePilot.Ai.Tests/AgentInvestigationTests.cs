using System.Text.Json;
using NodePilot.Ai.Agents;
using NodePilot.Core.Agents;
using Xunit;

namespace NodePilot.Ai.Tests;

public sealed class AgentInvestigationTests
{
    [Fact]
    public async Task ParallelUpdatesHaveContiguousRevisionsAndRetainEveryPatch()
    {
        var revisions = new List<int>();
        var evidence = new AgentEvidenceStore((_, _) => Task.CompletedTask);
        var register = new AgentInvestigation([new() { Id = "reader" }], evidence, async (e, _) => {
            await Task.Yield();
            revisions.Add(JsonDocument.Parse(e.Content).RootElement.GetProperty("revision").GetInt32());
        }, s => s, _ => { });
        var update = register.Tools("reader").Single(t => t.Name == "investigation_update");
        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => update.InvokeAsync(JsonSerializer.SerializeToElement(new {
            id = "check" + i, question = "Question " + i, nextCheck = "Read source"
        }), TestContext.Current.CancellationToken)));
        Assert.Equal(Enumerable.Range(1, 20), revisions);
        Assert.Equal(20, register.Checks().GetArrayLength());
    }

    [Fact]
    public async Task ParallelEvidenceReservesQuotaAndUniqueIdsBeforePersistence()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var evidence = new AgentEvidenceStore(async (_, ct) => {
            Interlocked.Increment(ref entered);
            await release.Task.WaitAsync(ct);
        });
        var member = new AgentDefinition { Id = "reader" };
        var captures = Enumerable.Range(0, 16).Select(_ => evidence.CaptureAsync(member, "read", "", new string('x', 1_000_000), 16000, TestContext.Current.CancellationToken)).ToArray();
        Assert.Equal(16, entered);
        await Assert.ThrowsAsync<AgentBudgetExceededException>(() => evidence.CaptureAsync(member, "read", "", "x", 16000, TestContext.Current.CancellationToken));
        release.SetResult();
        var results = await Task.WhenAll(captures);
        Assert.Equal(16, results.Select(r => JsonDocument.Parse(r).RootElement.GetProperty("evidenceId").GetString()).Distinct().Count());
        Assert.Equal(16, evidence.Count);
    }

    [Fact]
    public async Task FailedEvidencePersistenceReturnsQuotaWithoutReusingIds()
    {
        var fail = true;
        var evidence = new AgentEvidenceStore((_, _) => fail ? throw new IOException("failed") : Task.CompletedTask);
        var member = new AgentDefinition { Id = "reader" };
        for (var i = 0; i < 20; i++)
            await Assert.ThrowsAsync<IOException>(() => evidence.CaptureAsync(member, "read", "", new string('x', 1_000_000), 16000, TestContext.Current.CancellationToken));
        fail = false;
        var result = await evidence.CaptureAsync(member, "read", "", "ok", 16000, TestContext.Current.CancellationToken);
        Assert.Contains("ev-00021", result);
        Assert.Equal(1, evidence.Count);
    }
    [Fact]
    public async Task FocusedPatchPreservesQuestionAndEvidenceAndClosedReplyProvidesRecovery()
    {
        var evidence = new AgentEvidenceStore((_, _) => Task.CompletedTask);
        AgentDefinition[] members = [new() { Id = "reader" }];
        var invalidations = 0;
        var board = new AgentInvestigation(members, evidence, (_, _) => Task.CompletedTask, s => s, _ => invalidations++);
        var tool = board.Tools("reader").Single(t => t.Name == "investigation_update");
        await tool.InvokeAsync(Check(), CancellationToken.None);
        await evidence.CaptureAsync(members[0], "read", "{}", "Observed gamma missing", 16000, CancellationToken.None);
        await tool.InvokeAsync(JsonSerializer.SerializeToElement(new {
            id = "compare", status = "resolved", evidenceIds = new[] { "ev-00001" }, conclusion = "Gamma missing"
        }), CancellationToken.None);
        var revision = board.Revision;
        var response = await tool.InvokeAsync(JsonSerializer.SerializeToElement(new {
            id = "compare", conclusion = "Reviewer approves gamma missing"
        }), CancellationToken.None);
        using var reply = JsonDocument.Parse(response);
        Assert.False(reply.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal("closed_check", reply.RootElement.GetProperty("code").GetString());
        Assert.Equal("Gamma missing", reply.RootElement.GetProperty("current").GetProperty("conclusion").GetString());
        Assert.Equal(revision, board.Revision);
        Assert.Equal(2, invalidations);
        await tool.InvokeAsync(JsonSerializer.SerializeToElement(new {
            id = "compare", status = "open", nextCheck = "Read counterpart"
        }), CancellationToken.None);
        Assert.NotNull(board.Blockers);
    }
    [Fact]
    public async Task ReassigningClosedCheckPreservesReviewWhileConclusionChangesRequireReopening()
    {
        var evidence = new AgentEvidenceStore((_, _) => Task.CompletedTask);
        AgentDefinition[] members = [new() { Id = "reader" }, new() { Id = "lead" }];
        var invalidations = 0;
        var board = new AgentInvestigation(members, evidence, (_, _) => Task.CompletedTask, s => s, _ => invalidations++);
        await evidence.CaptureAsync(members[0], "read", "{}", "Original", 16000, CancellationToken.None);
        var update = board.Tools("lead").Single(t => t.Name == "investigation_update");
        var check = JsonSerializer.Deserialize<Dictionary<string, object>>(Check("resolved", ["ev-00001"]).GetRawText())!;
        await update.InvokeAsync(JsonSerializer.SerializeToElement(check), CancellationToken.None);
        check["owner"] = "lead";
        await update.InvokeAsync(JsonSerializer.SerializeToElement(check), CancellationToken.None);
        Assert.Equal(1, invalidations);
        Assert.Equal(2, board.Revision);
        check["conclusion"] = "New unsupported finding";
        Assert.Contains("\"accepted\":false", await update.InvokeAsync(JsonSerializer.SerializeToElement(check), CancellationToken.None));
        Assert.Equal(2, board.Revision);
    }

    [Fact]
    public async Task UpdatesAreRedactedAndCannotReferenceAnotherRunsEvidenceOrTarget()
    {
        var events = new List<AgentProgress>();
        var store = new AgentEvidenceStore((_, _) => Task.CompletedTask);
        var board = new AgentInvestigation([new() { Id = "reader" }], store,
            (e, _) => { events.Add(e); return Task.CompletedTask; }, s => s.Replace("SECRET", "[redacted]"), _ => { });
        var tool = board.Tools("reader").Single(t => t.Name == "investigation_update");
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(Check().GetRawText())!;
        fields["question"] = "Inspect SECRET";
        fields["owner"] = "foreign-target";
        await Assert.ThrowsAsync<ArgumentException>(() => tool.InvokeAsync(JsonSerializer.SerializeToElement(fields), CancellationToken.None));
        fields["owner"] = "reader";
        await tool.InvokeAsync(JsonSerializer.SerializeToElement(fields), CancellationToken.None);
        Assert.DoesNotContain("SECRET", Assert.Single(events).Content);
        Assert.Contains("redacted", board.Summary().GetRawText());
        await Assert.ThrowsAsync<ArgumentException>(() => tool.InvokeAsync(Check("resolved", ["ev-99999"]), CancellationToken.None));
        Assert.NotNull(board.Blockers);
    }

    [Fact]
    public async Task ClosedCheckCannotBeRewrittenWithoutReopeningAndInvalidatingReview()
    {
        var evidence = new AgentEvidenceStore((_, _) => Task.CompletedTask);
        AgentDefinition[] members = [new() { Id = "reader" }, new() { Id = "review", IsReviewer = true }];
        var completion = new TeamCompletionState(members);
        var board = new AgentInvestigation(members, evidence, (_, _) => Task.CompletedTask, s => s, completion.InvalidateReviews);
        var update = board.Tools("reader").Single(t => t.Name == "investigation_update");
        await evidence.CaptureAsync(members[0], "read", "{}", "Original comparison", 16000, CancellationToken.None);
        await update.InvokeAsync(Check("resolved", ["ev-00001"]), CancellationToken.None);
        completion.Record("review", "completed", "Approved");
        Assert.Null(completion.GetBlockers());
        var rewrite = JsonSerializer.Deserialize<Dictionary<string, object>>(Check("resolved", ["ev-00001"]).GetRawText())!;
        rewrite["conclusion"] = "Review approved the same conclusion";
        Assert.Contains("\"accepted\":false", await update.InvokeAsync(JsonSerializer.SerializeToElement(rewrite), CancellationToken.None));
        Assert.Null(completion.GetBlockers());
        await update.InvokeAsync(Check(), CancellationToken.None);
        Assert.NotNull(completion.GetBlockers());
        Assert.NotNull(board.Blockers);
    }

    [Fact]
    public async Task SharedCheckRequiresOriginalEvidenceAndRetainsCounterevidence()
    {
        var events = new List<AgentProgress>();
        var revisions = 0;
        var evidence = new AgentEvidenceStore((e, _) => { events.Add(e); return Task.CompletedTask; });
        AgentDefinition[] members = [new() { Id = "lead" }, new() { Id = "reader" }];
        var board = new AgentInvestigation(members, evidence, (e, _) => { events.Add(e); return Task.CompletedTask; }, s => s, _ => revisions++);
        var update = board.Tools("reader").Single(t => t.Name == "investigation_update");
        await update.InvokeAsync(Check(), CancellationToken.None);
        Assert.Contains("Compare all entries", board.Blockers);
        await Assert.ThrowsAsync<ArgumentException>(() => update.InvokeAsync(Check("resolved", ["ev-00001"]), CancellationToken.None));
        await evidence.CaptureAsync(members[1], "read", "{}", "Actual keys alpha, beta", 16000, CancellationToken.None);
        await evidence.CaptureAsync(members[0], "read", "{}", "Expected keys alpha, beta, gamma", 16000, CancellationToken.None);
        await update.InvokeAsync(Check("resolved", ["ev-00001"], ["ev-00002"]), CancellationToken.None);
        Assert.Null(board.Blockers);
        Assert.Equal(2, revisions);
        var read = board.Tools("lead").Single(t => t.Name == "investigation_read");
        var shared = await read.InvokeAsync(JsonSerializer.SerializeToElement(new { id = "compare" }), CancellationToken.None);
        Assert.Contains("ev-00002", shared);
        Assert.Equal(2, events.Count(e => e.Kind == "investigation_updated"));
        await update.InvokeAsync(Check("resolved", ["ev-00001"], ["ev-00002"]), CancellationToken.None);
        Assert.Equal(2, revisions);
        await update.InvokeAsync(Check(), CancellationToken.None);
        Assert.NotNull(board.Blockers);
    }

    [Fact]
    public async Task FailedPersistenceCannotCloseCheckAndNotesDoNotCreateEvidence()
    {
        var fail = false;
        var evidence = new AgentEvidenceStore((_, _) => Task.CompletedTask);
        var board = new AgentInvestigation([new() { Id = "reader" }], evidence,
            (_, _) => fail ? Task.FromException(new IOException("journal unavailable")) : Task.CompletedTask, s => s, _ => { });
        var update = board.Tools("reader").Single(t => t.Name == "investigation_update");
        await update.InvokeAsync(Check(), CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => update.InvokeAsync(Check("blocked"), CancellationToken.None));
        fail = true;
        await Assert.ThrowsAsync<IOException>(() => update.InvokeAsync(Check("blocked", limitation: "Required remote source is outside configured targets"), CancellationToken.None));
        Assert.NotNull(board.Blockers);
        Assert.Equal(0, evidence.Count);
        Assert.Equal(1, board.Revision);
    }

    internal static JsonElement Check(string status = "open", string[]? evidence = null, string[]? counter = null, string limitation = "") =>
        JsonSerializer.SerializeToElement(new { id = "compare", question = "Compare all entries", owner = "reader",
            hypothesis = "Representations may differ", evidenceIds = evidence ?? [], counterevidenceIds = counter ?? [],
            nextCheck = "Read both inventories", status, conclusion = status == "resolved" ? "Missing gamma in observed representation" : "", limitation });
}
