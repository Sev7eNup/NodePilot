using Xunit;

namespace NodePilot.Mcp.Tests.Infra;

/// <summary>
/// Test classes that read/write process-global environment variables (NODEPILOT_MCP_*) join this
/// collection so xUnit never runs them concurrently — otherwise one class's env mutation could
/// race another class's read.
/// </summary>
[CollectionDefinition("EnvMutating", DisableParallelization = true)]
public sealed class EnvMutatingCollection;
