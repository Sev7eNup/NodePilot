namespace NodePilot.Engine.Conditions;

/// <summary>
/// A condition expression is malformed: unknown node type or operator, a missing operand or
/// child, or a legacy string that is not <c>&lt;stepId&gt;.success|failed</c>. Such a condition
/// never opens an edge; the scheduler fails the run and names the edge.
/// </summary>
public sealed class ConditionEvaluationException(string message) : InvalidOperationException(message);
