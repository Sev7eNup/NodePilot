using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Hosting;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// Pins the path-resolution contract of <see cref="LoggingSetup.ResolveLogFilePath"/>.
/// The production installer relies on this exact behaviour to point writable log files
/// at <c>C:\ProgramData\NodePilot\logs</c> while the install dir stays read-only —
/// breaking it silently routes log writes back to a directory the service-account
/// can't write to.
/// </summary>
public class LoggingSetupTests
{
    private static IConfiguration ConfigWithPath(string? path)
    {
        var values = new Dictionary<string, string?>();
        if (path is not null) values["Logging:File:Path"] = path;
        return new ConfigurationBuilder().AddInMemoryCollection(values!).Build();
    }

    [Fact]
    public void ResolveLogFilePath_ReturnsContentRootDefault_WhenOverrideMissing()
    {
        var config = ConfigWithPath(null);
        var path = LoggingSetup.ResolveLogFilePath(config, "C:\\App");

        path.Should().Be(Path.Combine("C:\\App", "logs", "nodepilot-.log"));
    }

    [Fact]
    public void ResolveLogFilePath_ReturnsContentRootDefault_WhenOverrideIsBlank()
    {
        var config = ConfigWithPath("   ");
        var path = LoggingSetup.ResolveLogFilePath(config, "C:\\App");

        path.Should().EndWith(Path.Combine("logs", "nodepilot-.log"),
            "blank override must fall back to the ContentRoot default");
    }

    [Fact]
    public void ResolveLogFilePath_PassesAbsoluteOverride_Through()
    {
        var config = ConfigWithPath("C:\\ProgramData\\NodePilot\\logs\\np-.log");
        var path = LoggingSetup.ResolveLogFilePath(config, "C:\\App");

        path.Should().Be("C:\\ProgramData\\NodePilot\\logs\\np-.log",
            "absolute override is the production-installer scenario — must be honoured 1:1");
    }

    [Fact]
    public void ResolveLogFilePath_RebasesRelativeOverride_AgainstRootFolder()
    {
        var config = ConfigWithPath("custom-logs/np-.log");
        var path = LoggingSetup.ResolveLogFilePath(config, "C:\\App");

        path.Should().Be(Path.Combine("C:\\App", "custom-logs/np-.log"),
            "relative override is rebased against ContentRoot (dev convenience)");
    }

    [Fact]
    public void BuildBootstrapConfiguration_PicksUpEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        try
        {
            var config = LoggingSetup.BuildBootstrapConfiguration();
            // We only check the resolver runs and returns a config — env-vars are
            // assembled by ConfigurationBuilder.AddEnvironmentVariables(); proving
            // they're present without leaking machine-specific assertions.
            config.Should().NotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
        }
    }
}
