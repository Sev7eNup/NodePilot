using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Tests.TestSupport;

/// <summary>
/// Test double for <see cref="IMaintenanceWindowEvaluator"/>. Defaults to "allow everything"
/// so existing tests are unaffected; set <see cref="Verdict"/> to simulate an active window.
/// </summary>
public sealed class StubMaintenanceWindowEvaluator : IMaintenanceWindowEvaluator
{
    public MaintenanceEvaluation Verdict { get; set; } = MaintenanceEvaluation.Allowed;
    public IReadOnlyList<MaintenanceWindowSummary> Affecting { get; set; } = [];

    /// <summary>Shared allow-everything instance for tests that don't exercise the gate.</summary>
    public static StubMaintenanceWindowEvaluator AllowAll => new();

    /// <summary>An evaluator that blocks every workflow with the given window name.</summary>
    public static StubMaintenanceWindowEvaluator Blocking(string windowName = "Test Window", Guid? windowId = null)
        => new() { Verdict = new MaintenanceEvaluation(true, windowId ?? Guid.NewGuid(), windowName, null, MaintenanceMode.Blackout) };

    public MaintenanceEvaluation Evaluate(Guid workflowId, Guid folderId, DateTime nowUtc) => Verdict;

    public IReadOnlyList<MaintenanceWindowSummary> GetWindowsAffecting(Guid workflowId, Guid folderId, DateTime nowUtc) => Affecting;

    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
}
