namespace NodePilot.Engine.Activities;

/// <summary>
/// Shared recursion-guard constants for activities that spawn child workflow executions
/// (<see cref="StartWorkflowActivity"/>, <see cref="ForEachActivity"/>).
/// </summary>
public static class WorkflowRecursion
{
    public const int MaxCallDepth = 10;

    public const string CallDepthKey = "__callDepth";

    public const string ReservedPrefix = "__";

    /// <summary>
    /// True if the input-parameter name is reserved for engine bookkeeping. Reserved keys
    /// must never be settable from external trigger payloads, manual-run requests, webhook
    /// bodies, or CLI args — otherwise an attacker could reset <c>__callDepth=-1000</c>
    /// and bypass the recursion guard, or smuggle in other engine-internal state.
    /// Case-insensitive.
    /// </summary>
    public static bool IsReservedParameterName(string? name)
        => !string.IsNullOrEmpty(name)
           && name.StartsWith(ReservedPrefix, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the first reserved key found in <paramref name="parameters"/>, or null if
    /// none are present. Used by ingestion paths to reject the request with a clear error
    /// before any engine state gets touched.
    /// </summary>
    public static string? FindReservedKey(System.Collections.Generic.IEnumerable<string>? parameterNames)
    {
        if (parameterNames is null) return null;
        foreach (var name in parameterNames)
        {
            if (IsReservedParameterName(name)) return name;
        }
        return null;
    }
}
