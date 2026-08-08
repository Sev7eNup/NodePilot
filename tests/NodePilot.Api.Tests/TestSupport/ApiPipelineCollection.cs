using Xunit;

namespace NodePilot.Api.Tests.TestSupport;

/// <summary>
/// Serializes every test class that boots the full Program.cs host via
/// <see cref="ApiPipelineFactory"/>. Required because of a process-wide static in the
/// Serilog bootstrap: each host boot re-runs Program's top-level statements, which assign a
/// fresh ReloadableLogger to the static <c>Log.Logger</c>, and <c>UseSerilog</c> then
/// freezes whatever logger is CURRENT during that host's DI resolution. Two hosts booting
/// concurrently therefore race — host A freezes the bootstrap logger host B just installed,
/// and B's own freeze throws "The logger is already frozen", failing the boot. Sequential
/// boots are safe (fresh logger per Main run, one freeze each), so a shared xunit collection
/// — which runs its member classes one after another — is the entire fix. No fixture is
/// attached on purpose: each test still gets its own isolated factory + SQLite database.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiPipelineCollection
{
    public const string Name = "ApiPipelineSmoke";
}
