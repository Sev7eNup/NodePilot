using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// <see cref="NodePilot.Scheduler.Sources.ScheduleTriggerSource"/> enforces
/// <c>Trigger:Schedule:MaxActiveJobs</c> through a <b>process-static</b> counter, so every test
/// that starts a schedule source shares it. Both classes in this collection assert against that
/// cap — and xUnit parallelizes across classes by default, which makes one class's legitimately
/// held job slot look like a leaked one to the other.
///
/// <para>The symptom is order-dependent rather than deterministic: it only shows up when the
/// scheduler happens to overlap the two classes, so an unrelated test added anywhere in the
/// assembly can surface or hide it. Serializing them removes the coupling entirely.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ScheduleJobSlotCollection
{
    public const string Name = "schedule-job-slot";
}
