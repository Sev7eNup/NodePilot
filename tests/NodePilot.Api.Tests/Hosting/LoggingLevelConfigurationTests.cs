using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// <c>Logging:LogLevel:*</c> is exposed in appsettings.json and editable through the Admin
/// Settings page, but Serilog's <c>ReadFrom.Configuration</c> only reads its own
/// <c>Serilog:*</c> section, so these keys need a translation step to take effect.
/// <see cref="LoggingSetup.ApplyConfiguredLevels"/> does that translation; these tests pin
/// it, including the dampening defaults that must survive an empty section.
/// </summary>
public sealed class LoggingLevelConfigurationTests
{
    [Theory]
    [InlineData("Warning", LogEventLevel.Warning)]
    [InlineData("warning", LogEventLevel.Warning)]
    [InlineData("Debug", LogEventLevel.Debug)]
    [InlineData("Trace", LogEventLevel.Verbose)]
    [InlineData("Critical", LogEventLevel.Fatal)]
    [InlineData("Information", LogEventLevel.Information)]
    public void DefaultKey_SetsTheGlobalMinimumLevel(string configured, LogEventLevel expected)
    {
        using var logger = BuildLogger(new() { ["Logging:LogLevel:Default"] = configured });

        logger.IsEnabled(expected).Should().BeTrue(
            "the configured level itself must pass the global minimum");
        if (expected > LogEventLevel.Verbose)
            logger.IsEnabled(expected - 1).Should().BeFalse(
                "anything below the configured level must be filtered out");
    }

    [Fact]
    public void CategoryKey_OverridesThatCategoryOnly()
    {
        using var logger = BuildLogger(new()
        {
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:NodePilot.Engine"] = "Debug",
        });

        logger.ForContext(Constants.SourceContextPropertyName, "NodePilot.Engine")
            .IsEnabled(LogEventLevel.Debug).Should().BeTrue();
        logger.ForContext(Constants.SourceContextPropertyName, "NodePilot.Api")
            .IsEnabled(LogEventLevel.Debug).Should().BeFalse(
                "an override must not leak into sibling categories");
    }

    [Fact]
    public void EmptySection_KeepsTheFrameworkDampeningDefaults()
    {
        using var logger = BuildLogger(new() { ["Logging:LogLevel:Default"] = "Debug" });

        logger.ForContext(Constants.SourceContextPropertyName,
                "Microsoft.EntityFrameworkCore.Database.Command")
            .IsEnabled(LogEventLevel.Information).Should().BeFalse(
                "EF SQL dumps stay dampened even when the operator raises the global level — " +
                "they are what pushed CMTrace past its parse limit");
        logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.AspNetCore")
            .IsEnabled(LogEventLevel.Information).Should().BeFalse();
    }

    [Fact]
    public void ExplicitCategoryValue_WinsOverTheDampeningDefault()
    {
        using var logger = BuildLogger(new()
        {
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Information",
        });

        logger.ForContext(Constants.SourceContextPropertyName,
                "Microsoft.EntityFrameworkCore.Database.Command")
            .IsEnabled(LogEventLevel.Information).Should().BeTrue(
                "an operator who deliberately turns SQL logging back on must get it");
    }

    [Fact]
    public void NoneValue_SilencesTheCategoryEntirely()
    {
        using var logger = BuildLogger(new()
        {
            ["Logging:LogLevel:Default"] = "Debug",
            ["Logging:LogLevel:NodePilot.Noisy"] = "None",
        });

        logger.ForContext(Constants.SourceContextPropertyName, "NodePilot.Noisy")
            .IsEnabled(LogEventLevel.Fatal).Should().BeFalse();
    }

    [Fact]
    public void UnparsableValue_IsIgnoredAndDoesNotThrow()
    {
        var act = () => BuildLogger(new()
        {
            ["Logging:LogLevel:Default"] = "Loud",
            ["Logging:LogLevel:NodePilot.Engine"] = "alsoNotALevel",
        }).Dispose();

        act.Should().NotThrow("a typo in a log level must never stop the host from booting");
    }

    /// <summary>
    /// Guards the shipped defaults: every category appsettings.json configures must use a
    /// level the translator understands, otherwise the key stays silently inert.
    /// </summary>
    [Fact]
    public void ShippedAppSettings_UsesOnlyParsableLevels()
    {
        var appsettings = Path.Combine(FindRepoRoot(), "src", "NodePilot.Api", "appsettings.json");
        var configured = new ConfigurationBuilder().AddJsonFile(appsettings).Build()
            .GetSection("Logging:LogLevel").GetChildren().ToList();

        configured.Should().NotBeEmpty("appsettings.json ships a Logging:LogLevel section");

        using var logger = BuildLogger(configured.ToDictionary(
            c => $"Logging:LogLevel:{c.Key}", c => c.Value!));

        // Every shipped entry asks for Warning, so Information must be filtered out.
        logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.AspNetCore")
            .IsEnabled(LogEventLevel.Information).Should().BeFalse();
    }

    private static Logger BuildLogger(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var cfg = new LoggerConfiguration();
        LoggingSetup.ApplyConfiguredLevels(cfg, configuration);
        return cfg.CreateLogger();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NodePilot.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Could not locate NodePilot.slnx from the test output directory.");
    }
}
