namespace NodePilot.Api.Security;

/// <summary>
/// Marks an endpoint whose HTTP semantics require the active cluster leader even when the
/// transport method is normally read-only. The leader middleware enforces this metadata before
/// the endpoint can persist or dispatch work.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class LeaderOnlyAttribute : Attribute
{
}
