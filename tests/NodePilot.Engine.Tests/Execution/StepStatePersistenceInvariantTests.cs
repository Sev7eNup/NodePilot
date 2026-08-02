using FluentAssertions;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

/// <summary>
/// Locks in the fix for a duplicate-INSERT defect found in the field on 2026-08-02
/// (~1200 primary-key violations per day on SQL Server, surfacing as sporadically failed
/// steps in workflows that use a junction).
///
/// <para>
/// Mechanism: a junction cancels the losing branches. When that token tripped *inside*
/// <c>SaveChangesAsync</c> after the INSERT had already committed, EF Core left the entity
/// in the <c>Added</c> state — it only accepts changes on successful completion. The
/// cancellation handler then saved again, EF re-issued the INSERT, and the database
/// rejected it. Postgres never showed the symptom because Npgsql aborts the command
/// server-side on cancel, so nothing was committed to collide with; the defect was
/// therefore invisible in development and only appeared on the SQL Server deployment.
/// </para>
///
/// <para>
/// The invariant: a step's own lifecycle rows are written with
/// <c>CancellationToken.None</c>. They must land even while the branch is being torn down —
/// which is exactly why the cancelled/failed handlers already did so. This is a source-level
/// guard because the race needs a cancellation to land inside a committed database round
/// trip, which is not reproducible in a unit test.
/// </para>
/// </summary>
public sealed class StepStatePersistenceInvariantTests
{
    [Theory]
    [InlineData("step.running")]
    [InlineData("step.terminal")]
    [InlineData("step.cancelled")]
    [InlineData("step.failed")]
    public void StepLifecycleWrites_AreNotCancellable(string operation)
    {
        var source = ReadStepRunnerSource();

        var call = source.IndexOf($"SaveChangesMeasuredAsync(\"{operation}\"", StringComparison.Ordinal);
        call.Should().BeGreaterThanOrEqualTo(0, $"StepRunner must still persist the '{operation}' state");

        var argumentsEnd = source.IndexOf(')', call);
        argumentsEnd.Should().BeGreaterThan(call);
        var arguments = source[call..argumentsEnd];

        arguments.Should().Contain("CancellationToken.None",
            $"the '{operation}' write must survive branch cancellation; passing the step token " +
            "lets the token trip after the INSERT commits, which leaves the entity Added and " +
            "makes the next save re-insert it");
    }

    [Fact]
    public void StepRunner_DoesNotPassTheStepTokenToAnyStepStateWrite()
    {
        var source = ReadStepRunnerSource();

        source.Should().NotContain("SaveChangesMeasuredAsync(\"step.terminal\", ct)");
        source.Should().NotContain("SaveChangesMeasuredAsync(\"step.running\", ct)");
    }

    private static string ReadStepRunnerSource() => File.ReadAllText(Path.Combine(
        FindRepoRoot(), "src", "NodePilot.Engine", "Execution", "StepRunner.cs"));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NodePilot.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Could not locate NodePilot.slnx from the test output directory.");
    }
}
