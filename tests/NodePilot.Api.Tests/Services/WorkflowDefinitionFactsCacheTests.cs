using FluentAssertions;
using NodePilot.Api.Services;
using NodePilot.Core.Operations;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Api.Tests.Services;

public class WorkflowDefinitionFactsCacheTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime Rev1 = new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Rev2 = new(2026, 8, 12, 11, 0, 0, DateTimeKind.Utc);

    private static WorkflowDefinitionFacts Facts(string reference)
        => new([new WorkflowCallSite("startWorkflow", reference)], [], false);

    [Fact]
    public void StaleIds_UnseenWorkflow_IsStale()
    {
        new WorkflowDefinitionFactsCache().StaleIds([(A, Rev1)]).Should().Equal(A);
    }

    [Fact]
    public void StaleIds_UnchangedRevision_IsNotStale()
    {
        // The whole point: the steady state of a 5 s poll must read zero definitions.
        var cache = new WorkflowDefinitionFactsCache();
        cache.Store(A, Rev1, Facts("Child"));

        cache.StaleIds([(A, Rev1)]).Should().BeEmpty();
    }

    [Fact]
    public void StaleIds_RevisionMovedBackwards_IsStillStale()
    {
        // A rollback restores an older definition and moves UpdatedAt backwards. The stamp is a
        // revision marker, not a clock — comparing "newer than" would serve the rolled-back
        // workflow its abandoned call graph until somebody saved it again.
        var cache = new WorkflowDefinitionFactsCache();
        cache.Store(A, Rev2, Facts("Child"));

        cache.StaleIds([(A, Rev1)]).Should().Equal(A);
    }

    [Fact]
    public void StaleIds_ReportsOnlyTheChangedWorkflows()
    {
        var cache = new WorkflowDefinitionFactsCache();
        cache.Store(A, Rev1, Facts("Child"));
        cache.Store(B, Rev1, Facts("Other"));

        cache.StaleIds([(A, Rev1), (B, Rev2)]).Should().Equal(B);
    }

    [Fact]
    public void Get_ReturnsWhatWasStored_AndEmptyForAnUnknownWorkflow()
    {
        var cache = new WorkflowDefinitionFactsCache();
        cache.Store(A, Rev1, Facts("Child"));

        cache.Get(A).CallSites.Should().Equal(new WorkflowCallSite("startWorkflow", "Child"));
        // "Never seen" and "definition holds none of this" both mean nothing to show — neither
        // may throw.
        cache.Get(B).Should().BeSameAs(WorkflowDefinitionFacts.Empty);
    }

    [Fact]
    public void Store_NewerRevision_ReplacesTheOldFacts()
    {
        var cache = new WorkflowDefinitionFactsCache();
        cache.Store(A, Rev1, Facts("Child"));
        cache.Store(A, Rev2, Facts("Renamed"));

        cache.Get(A).CallSites.Should().Equal(new WorkflowCallSite("startWorkflow", "Renamed"));
        cache.StaleIds([(A, Rev2)]).Should().BeEmpty();
    }

    [Fact]
    public void Store_PastTheEntryCeiling_EvictsTheOldestAndKeepsTheRest()
    {
        // Nothing tells this cache about a deleted workflow, so it needs a bound. Evicting
        // everything at once would thrash forever, reloading every definition on each poll. A is
        // stored first, so it is among the oldest; the newest entries must survive eviction.
        var cache = new WorkflowDefinitionFactsCache();
        cache.Store(A, Rev1, Facts("Child"));

        var newest = new List<Guid>();
        for (var i = 0; i < 9000; i++)
        {
            var id = Guid.NewGuid();
            cache.Store(id, Rev1, Facts("Child"));
            if (i >= 8000) newest.Add(id);
        }

        cache.Get(A).CallSites.Should().BeEmpty();             // oldest: evicted, costs one re-read
        cache.StaleIds(newest.Select(id => (id, Rev1))).Should().BeEmpty();  // newest: still cached
    }

    [Fact]
    public async Task ResolveAsync_SteadyState_ReadsNoDefinitions()
    {
        var cache = new WorkflowDefinitionFactsCache();
        cache.Store(A, Rev1, Facts("Child"));
        var reads = 0;

        var resolved = await cache.ResolveAsync(
            [(A, Rev1)],
            (ids, _) => { reads++; return Task.FromResult<IReadOnlyList<WorkflowDefinitionRow>>([]); },
            TestContext.Current.CancellationToken);

        reads.Should().Be(0);
        resolved[A].CallSites.Should().Equal(new WorkflowCallSite("startWorkflow", "Child"));
    }

    [Fact]
    public async Task ResolveAsync_ChangedRevision_RereadsOnlyThatDefinition()
    {
        var cache = new WorkflowDefinitionFactsCache();
        cache.Store(A, Rev1, Facts("Child"));
        cache.Store(B, Rev1, Facts("Other"));
        var requested = new List<Guid>();

        const string definition = """
            {"nodes":[{"id":"n1","type":"activity","data":{"activityType":"startWorkflow","config":{"workflowNameOrId":"Renamed"}}}],"edges":[]}
            """;
        var resolved = await cache.ResolveAsync(
            [(A, Rev1), (B, Rev2)],
            (ids, _) =>
            {
                requested.AddRange(ids);
                return Task.FromResult<IReadOnlyList<WorkflowDefinitionRow>>(
                    [new WorkflowDefinitionRow(B, Rev2, definition)]);
            },
            TestContext.Current.CancellationToken);

        requested.Should().Equal(B);
        resolved[A].CallSites.Should().Equal(new WorkflowCallSite("startWorkflow", "Child"));
        resolved[B].CallSites.Should().Equal(new WorkflowCallSite("startWorkflow", "Renamed"));
        // Stored under the new revision, so the next request reads nothing.
        cache.StaleIds([(B, Rev2)]).Should().BeEmpty();
    }
}
