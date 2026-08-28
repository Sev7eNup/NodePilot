using Xunit;

namespace NodePilot.Engine.Tests;

/// <summary>
/// Defines the "SerialEngineTests" collection explicitly. Classes in the collection run
/// sequentially because they modify process-wide WorkflowEngine state such as the execution
/// registry, capacity gate, and debug handles.
/// two engine tests in parallel would race on it. Deliberately NOT
/// <c>DisableParallelization</c>: the collection may still run alongside OTHER collections.
/// </summary>
[CollectionDefinition("SerialEngineTests")]
public sealed class SerialEngineTestCollection;
