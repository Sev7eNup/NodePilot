using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;

namespace NodePilot.TestCommons;

/// <summary>
/// Test double for <see cref="IMaintenanceWindowEvaluator"/> (declared in NodePilot.Core, hence
/// shared here between Api.Tests and Engine.Tests). Defaults to "allow everything"
/// so existing tests are unaffected; set <see cref="Verdict"/> to simulate an active window.
/// </summary>
public sealed class StubMaintenanceWindowEvaluator : IMaintenanceWindowEvaluator
{
    public MaintenanceEvaluation Verdict { get; set; } = MaintenanceEvaluation.Allowed;
    public IReadOnlyList<MaintenanceWindowSummary> Affecting { get; set; } = [];

    /// <summary>
    /// Time-dependent verdict, taking precedence over <see cref="Verdict"/> when set. The real
    /// evaluator is a pure function of its <c>nowUtc</c> argument, so callers that ask about a
    /// *future* moment (e.g. the dashboard evaluating a trigger's predicted fire time) get a
    /// different answer than callers asking about now. Tests that need to tell those two apart
    /// set this instead of the fixed verdict.
    /// </summary>
    public Func<DateTime, MaintenanceEvaluation>? VerdictAt { get; set; }

    /// <summary>Records every (workflowId, folderId, nowUtc) the subject evaluated.</summary>
    public List<(Guid WorkflowId, Guid FolderId, DateTime NowUtc)> Calls { get; } = [];

    /// <summary>
    /// Hook invoked on every evaluation, before the verdict is returned — used by the trigger
    /// orchestrator tests to inject leadership changes mid-pass.
    /// </summary>
    public Action? OnEvaluate { get; set; }

    /// <summary>Shared allow-everything instance for tests that don't exercise the gate.</summary>
    public static StubMaintenanceWindowEvaluator AllowAll => new();

    /// <summary>An evaluator that blocks every workflow with the given window name.</summary>
    public static StubMaintenanceWindowEvaluator Blocking(string windowName = "Test Window", Guid? windowId = null)
        => new() { Verdict = new MaintenanceEvaluation(true, windowId ?? Guid.NewGuid(), windowName, null, MaintenanceMode.Blackout) };

    public MaintenanceEvaluation Evaluate(Guid workflowId, Guid folderId, DateTime nowUtc)
    {
        Calls.Add((workflowId, folderId, nowUtc));
        OnEvaluate?.Invoke();
        return VerdictAt is not null ? VerdictAt(nowUtc) : Verdict;
    }

    public IReadOnlyList<MaintenanceWindowSummary> GetWindowsAffecting(Guid workflowId, Guid folderId, DateTime nowUtc) => Affecting;

    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
}
