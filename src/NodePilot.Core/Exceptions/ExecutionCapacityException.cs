namespace NodePilot.Core.Exceptions;

/// <summary>
/// Thrown when an execute call would exceed the global or per-user cap on concurrently
/// running workflow executions (this cap was added in response to a security audit
/// finding, tracked as finding H-3). Callers (controllers, trigger sources, fire-and-forget
/// webhook dispatch) should map this to HTTP 503/429 instead of letting it leak out as a
/// generic 500 — the API-side <c>CapacityExceptionHandler</c> handles that mapping
/// centrally; fire-and-forget paths just log the exception.
/// </summary>
public sealed class ExecutionCapacityException : Exception
{
    public ExecutionCapacityException(string message) : base(message) { }
}
