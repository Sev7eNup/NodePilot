using Xunit;

namespace NodePilot.Engine.Tests;

/// <summary>
/// Explicit definition for the "SerialEngineTests" collection. The 11 classes carrying
/// <c>[Collection("SerialEngineTests")]</c> previously relied on xUnit's implicit
/// collection (it works, but the intent lived nowhere and no flags could ever be attached).
/// Semantics stay exactly what the implicit collection provided: classes in the collection
/// run sequentially relative to each other because they drive the process-global
/// WorkflowEngine state (running-execution registry, capacity gate, debug handles) —
/// two engine tests in parallel would race on it. Deliberately NOT
/// <c>DisableParallelization</c>: the collection may still run alongside OTHER collections.
/// </summary>
[CollectionDefinition("SerialEngineTests")]
public sealed class SerialEngineTestCollection;
