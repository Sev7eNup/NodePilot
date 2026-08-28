namespace NodePilot.Core.Validation;

/// <summary>
/// The range rule for <c>Workflow.MaxConcurrentExecutions</c>. Lives in Core because three
/// write paths set the column and only one of them is the dedicated endpoint: workflow import
/// and backup restore write the entity directly, so a check that sat in the controller would
/// let a crafted file past.
/// </summary>
public static class WorkflowConcurrency
{
    public const int MinLimit = 1;

    /// <summary>
    /// Above the shipped global cap (500) and the dispatch worker count, so a value near the
    /// ceiling is inert rather than harmful.
    /// </summary>
    public const int MaxLimit = 1000;

    /// <summary>
    /// Returns null when the value is acceptable, otherwise a message naming the problem.
    /// Null itself is valid and means unlimited.
    /// </summary>
    public static string? Validate(int? limit)
    {
        if (limit is not { } value) return null;

        // Zero is rejected rather than treated as unlimited. Engine:MaxConcurrentExecutions
        // reads a non-positive cap as "off", so accepting 0 here would give the same number two
        // opposite meanings in one product. Disabling a workflow is the way to stop it running.
        if (value == 0)
            return "A concurrency limit of 0 is not allowed. Omit the limit for unlimited, or disable the workflow to stop it running.";

        return value < MinLimit || value > MaxLimit
            ? $"Concurrency limit must be between {MinLimit} and {MaxLimit}, or absent for unlimited."
            : null;
    }
}
