using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Hosting;
using NodePilot.Api.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// <c>Logging:LogLevel:*</c> is exposed in appsettings.json and editable through
/// Admin → Settings, but Serilog's <c>ReadFrom.Configuration</c> only reads its own
/// <c>Serilog:*</c> section — so the keys used to be inert while still reporting success to
/// the operator. <see cref="LoggingSetup.ApplyConfiguredLevels"/> translates them; these tests
/// pin that translation, including the dampening defaults that must survive an empty section.
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
    /// Guards the actual shipped defaults: every category appsettings.json configures must be
    /// a level the translator understands, otherwise the key is silently inert again.
    /// </summary>
    [Fact]
    public void ShippedAppSettings_UsesOnlyParsableLevels()
    {
        var appsettings = Path.Combine(FindRepoRoot(), "src", "NodePilot.Api", "appsettings.json");
        var configured = new ConfigurationBuilder().AddJsonFile(appsettings).Build()
            .GetSection("Logging:LogLevel").GetChildren().ToList();

        configured.Should().NotBeEmpty("appsettings.json ships a Logging:LogLevel section");

        using var logger = BuildLogger(configured.ToDictionary(
            c => $"Logging:LogLevel:{c.Key}", c => c.Value));

        logger.ForContext(Constants.SourceContextPropertyName, "NodePilot.Api")
            .IsEnabled(LogEventLevel.Information).Should().BeTrue(
                "NodePilot support and SIEM events are emitted at Information");
        // Framework categories remain dampened independently of the NodePilot default.
        logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.AspNetCore")
            .IsEnabled(LogEventLevel.Information).Should().BeFalse();
    }

    [Theory]
    [InlineData("deploy", "templates", "appsettings.Production.json.template")]
    [InlineData("deploy", "desktop", "appsettings.Desktop.json.template")]
    public void ProductionTemplates_PreserveNodePilotInformationEvents_AndDampenFrameworkNoise(
        params string[] relativePath)
    {
        var path = Path.Combine([FindRepoRoot(), .. relativePath]);
        // Installer templates still contain {{PLACEHOLDER}} tokens and are not JSON until the
        // installer renders them. Inspect only the stable LogLevel block; ApplyConfiguredLevels
        // itself is covered by the executable tests above.
        var template = File.ReadAllText(path);
        var blockStart = template.IndexOf("\"LogLevel\"", StringComparison.Ordinal);
        var blockEnd = template.IndexOf("},", blockStart, StringComparison.Ordinal);
        var logLevelBlock = template[blockStart..blockEnd];

        logLevelBlock.Should().Contain("\"Default\": \"Information\"",
            "production support and SIEM events are emitted at Information");
        logLevelBlock.Should().Contain("\"Microsoft.AspNetCore\": \"Warning\"");
        logLevelBlock.Should().Contain("\"Microsoft.EntityFrameworkCore.Database.Command\": \"Warning\"");
    }

    [Fact]
    public void SiemSmoke_InformationAuditEvent_ReachesParseableEcsFile()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"nodepilot-siem-smoke-{Guid.NewGuid():N}.ndjson");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Information",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
            }).Build();
            var cfg = new LoggerConfiguration();
            LoggingSetup.ApplyConfiguredLevels(cfg, configuration);
            cfg
                .Enrich.WithProperty("service.name", "nodepilot-api")
                .Enrich.WithProperty("deployment.environment", "poc-smoke")
                .WriteTo.File(new EcsJsonFormatter(), path);

            using (var logger = cfg.CreateLogger())
            {
                logger
                    .ForContext(Constants.SourceContextPropertyName, "NodePilot.Api.Audit")
                    .ForContext("event.action", "POC_SIEM_SMOKE")
                    .ForContext("event.category", "configuration")
                    .ForContext("event.outcome", "success")
                    .ForContext("ExecutionId", "11111111-1111-1111-1111-111111111111")
                    .Information("NodePilot SIEM smoke event");
            }

            var lines = File.ReadAllLines(path);
            lines.Should().ContainSingle(
                "an Information-level NodePilot event must survive the production minimum level");
            using var document = JsonDocument.Parse(lines[0]);
            var json = document.RootElement;
            json.GetProperty("log.level").GetString().Should().Be("info");
            json.GetProperty("message").GetString().Should().Be("NodePilot SIEM smoke event");
            json.GetProperty("event").GetProperty("action").GetString().Should().Be("POC_SIEM_SMOKE");
            json.GetProperty("service").GetProperty("name").GetString().Should().Be("nodepilot-api");
            json.GetProperty("nodepilot").GetProperty("execution_id").GetString()
                .Should().Be("11111111-1111-1111-1111-111111111111");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
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
