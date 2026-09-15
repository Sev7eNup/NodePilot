using Xunit;

namespace NodePilot.Api.Tests.Hubs;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExecutionHubStaticStateCollection
{
    public const string Name = "ExecutionHub static state";
}
