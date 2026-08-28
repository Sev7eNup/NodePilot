using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Data.Availability;

namespace NodePilot.TestCommons;

/// <summary>
/// Database-availability breakers for tests.
///
/// <para>Every hosted service now takes <see cref="IDatabaseAvailability"/> so it can park instead
/// of
/// hammering a database that is gone. Tests that exercise the service's own logic want that gate
/// open
/// and out of the way — that is <see cref="Available"/>. Tests that want to prove the gate actually
/// holds use <see cref="Unavailable"/>.</para>
/// </summary>
public static class TestDatabaseAvailability
{
    /// <summary>
    /// A breaker that is always servable. Note this is a real <see
    /// cref="DatabaseAvailabilityTracker"/>
    /// rather than a stub: a stub that always returned <c>true</c> would keep passing if
    /// <c>WaitUntilServableAsync</c> were changed to throw on cancellation, which is the one
    /// behaviour
    /// ~18 call sites depend on (they are written as <c>if (!await …) break;</c>).
    /// </summary>
    public static IDatabaseAvailability Available => new DatabaseAvailabilityTracker(
        NullLogger<DatabaseAvailabilityTracker>.Instance);

    /// <summary>A breaker whose gate is closed, for asserting that a loop parks rather than
    /// proceeds.</summary>
    public static IDatabaseAvailability Unavailable
    {
        get
        {
            var tracker = new DatabaseAvailabilityTracker(NullLogger<DatabaseAvailabilityTracker>.Instance);
            tracker.MarkBootComplete();
            tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);
            return tracker;
        }
    }
}
