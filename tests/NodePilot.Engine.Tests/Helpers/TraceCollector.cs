using System.Diagnostics;

namespace NodePilot.Engine.Tests.Helpers;

/// <summary>
/// Captures every Activity started/stopped against the given ActivitySource names.
/// Uses <see cref="ActivityListener"/> (BCL, no OTel dependency).
/// </summary>
public sealed class TraceCollector : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _stopped = new();
    private readonly object _sync = new();

    public IReadOnlyList<Activity> StoppedActivities
    {
        get { lock (_sync) return _stopped.ToArray(); }
    }

    public TraceCollector(params string[] sourceNames)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => Array.IndexOf(sourceNames, src.Name) >= 0,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (_sync) _stopped.Add(a);
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public Activity? Single(string operationName) =>
        StoppedActivities.FirstOrDefault(a => a.OperationName == operationName);

    public IEnumerable<Activity> Where(string operationName) =>
        StoppedActivities.Where(a => a.OperationName == operationName);

    public void Dispose() => _listener.Dispose();
}
