using FluentAssertions;
using NodePilot.Api.Services;
using NodePilot.Core.Operations;
using Xunit;

namespace NodePilot.Api.Tests.Services;

public class WorkflowCallSiteCacheTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime Rev1 = new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Rev2 = new(2026, 8, 12, 11, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<WorkflowCallSite> Sites(string reference)
        => [new WorkflowCallSite("startWorkflow", reference)];

    [Fact]
    public void StaleIds_UnseenWorkflow_IsStale()
    {
        new WorkflowCallSiteCache().StaleIds([(A, Rev1)]).Should().Equal(A);
    }

    [Fact]
    public void StaleIds_UnchangedRevision_IsNotStale()
    {
        // The whole point: the steady state of a 5 s poll must read zero definitions.
        var cache = new WorkflowCallSiteCache();
        cache.Store(A, Rev1, Sites("Child"));

        cache.StaleIds([(A, Rev1)]).Should().BeEmpty();
    }

    [Fact]
    public void StaleIds_RevisionMovedBackwards_IsStillStale()
    {
        // A rollback restores an older definition and moves UpdatedAt backwards. The stamp is a
        // revision marker, not a clock — comparing "newer than" would serve the rolled-back
        // workflow its abandoned call graph until somebody saved it again.
        var cache = new WorkflowCallSiteCache();
        cache.Store(A, Rev2, Sites("Child"));

        cache.StaleIds([(A, Rev1)]).Should().Equal(A);
    }

    [Fact]
    public void StaleIds_ReportsOnlyTheChangedWorkflows()
    {
        var cache = new WorkflowCallSiteCache();
        cache.Store(A, Rev1, Sites("Child"));
        cache.Store(B, Rev1, Sites("Other"));

        cache.StaleIds([(A, Rev1), (B, Rev2)]).Should().Equal(B);
    }

    [Fact]
    public void Get_ReturnsWhatWasStored_AndEmptyForAnUnknownWorkflow()
    {
        var cache = new WorkflowCallSiteCache();
        cache.Store(A, Rev1, Sites("Child"));

        cache.Get(A).Should().Equal(new WorkflowCallSite("startWorkflow", "Child"));
        // "Never seen" and "definition holds no calls" both mean no edges — neither may throw.
        cache.Get(B).Should().BeEmpty();
    }

    [Fact]
    public void Store_NewerRevision_ReplacesTheOldCallSites()
    {
        var cache = new WorkflowCallSiteCache();
        cache.Store(A, Rev1, Sites("Child"));
        cache.Store(A, Rev2, Sites("Renamed"));

        cache.Get(A).Should().Equal(new WorkflowCallSite("startWorkflow", "Renamed"));
        cache.StaleIds([(A, Rev2)]).Should().BeEmpty();
    }

    [Fact]
    public void Store_PastTheEntryCeiling_EvictsTheOldestAndKeepsTheRest()
    {
        // Nothing tells this cache about a deleted workflow, so it needs a bound. What it must NOT do
        // is drop everything: one workflow past the ceiling would then thrash forever, reloading every
        // definition each poll and wiping the map again on the way out. A is stored first and is
        // therefore among the oldest; the newest entries have to survive.
        var cache = new WorkflowCallSiteCache();
        cache.Store(A, Rev1, Sites("Child"));

        var newest = new List<Guid>();
        for (var i = 0; i < 9000; i++)
        {
            var id = Guid.NewGuid();
            cache.Store(id, Rev1, Sites("Child"));
            if (i >= 8000) newest.Add(id);
        }

        cache.Get(A).Should().BeEmpty();                       // oldest: evicted, costs one re-read
        cache.StaleIds(newest.Select(id => (id, Rev1))).Should().BeEmpty();  // newest: still cached
    }
}
