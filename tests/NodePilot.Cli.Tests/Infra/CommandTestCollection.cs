using Xunit;

namespace NodePilot.Cli.Tests.Infra;

/// <summary>
/// Serializes every test class that drives a real <see cref="CommandTestHarness"/>. The
/// harness mutates global console state (<c>Console.SetOut</c>) for the duration of one
/// invocation, so two tests running concurrently swap each other's <see cref="StringWriter"/>
/// and trigger <see cref="ObjectDisposedException"/> in the loser. xUnit collections with
/// no test class get a disable-parallelization marker via this fixture-less stub; both
/// integration classes opt in via <c>[Collection(CommandTestCollection.Name)]</c>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CommandTestCollection
{
    public const string Name = "CommandHarness";
}
