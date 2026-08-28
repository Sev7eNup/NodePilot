using Xunit;

namespace NodePilot.Api.Tests.TestSupport;

/// <summary>
/// Serializes every test class that boots the full Program.cs host via
/// <see cref="ApiPipelineFactory"/>. Program.cs assigns a fresh ReloadableLogger to the
/// static <c>Log.Logger</c> on each boot, and <c>UseSerilog</c> freezes whichever logger is
/// current at that moment. Concurrent boots race and can throw "The logger is already
/// frozen"; this collection runs its member classes one after another so each host freezes
/// its own logger.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiPipelineCollection
{
    public const string Name = "ApiPipelineSmoke";
}
