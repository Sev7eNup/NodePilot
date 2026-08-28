namespace NodePilot.Core.Exceptions;

/// <summary>
/// Thrown when an execute call would exceed the global or per-user cap on concurrently
/// running workflow executions. Callers (controllers, trigger sources, fire-and-forget webhook
/// dispatch) map this to HTTP 503/429 instead of a generic 500; the API-side
/// <c>CapacityExceptionHandler</c> does that centrally, and fire-and-forget paths log it.
/// </summary>
public sealed class ExecutionCapacityException : Exception
{
    public ExecutionCapacityException(string message) : base(message) { }
}
