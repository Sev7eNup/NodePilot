using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using NodePilot.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace NodePilot.Api.Tests.Telemetry;

/// <summary>
/// Boots the full OpenTelemetry service wiring through <see cref="OpenTelemetryExtensions"/>.
/// The sampler switch and OTLP protocol/header application run inside deferred configure
/// callbacks that only execute when the SDK builds the Tracer/Meter providers, so each test
/// resolves the providers to force those code paths. This guards against a bad sampler mode
/// or malformed OTLP endpoint taking the whole API host down at startup.
/// </summary>
public class OpenTelemetryExtensionsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IHostEnvironment Env() => new StubEnv();

    private sealed class StubEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "NodePilot.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public void AddNodePilotTelemetry_Disabled_StillRegistersPrometheusClientButNoProviders()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Env());

        services.AddNodePilotTelemetry(Config(new() { ["OpenTelemetry:Enabled"] = "false" }), Env());

        using var sp = services.BuildServiceProvider();
        // PrometheusClient is always registered so ObservabilityController can 503 gracefully.
        sp.GetService<PrometheusClient>().Should().NotBeNull();
        // No OTel pipeline when disabled.
        sp.GetService<TracerProvider>().Should().BeNull();
        sp.GetService<MeterProvider>().Should().BeNull();
    }

    [Theory]
    [InlineData("AlwaysOn")]
    [InlineData("AlwaysOff")]
    [InlineData("TraceIdRatio")]
    [InlineData("ParentBased")]
    public void AddNodePilotTelemetry_EachSamplerMode_BuildsTracerProvider(string mode)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Env());

        services.AddNodePilotTelemetry(
            Config(new()
            {
                ["OpenTelemetry:Enabled"] = "true",
                ["OpenTelemetry:Exporters:Traces"] = "true",
                ["OpenTelemetry:Exporters:Metrics"] = "false",
                ["OpenTelemetry:Exporters:Logs"] = "false",
                ["OpenTelemetry:Sampling:Mode"] = mode,
                ["OpenTelemetry:Sampling:Ratio"] = "0.25",
                ["OpenTelemetry:Otlp:Endpoint"] = "http://collector.example:4318",
                ["OpenTelemetry:Otlp:Protocol"] = "http/protobuf",
                ["OpenTelemetry:Otlp:Headers"] = "x-api-key=abc",
            }),
            Env());

        using var sp = services.BuildServiceProvider();
        // Resolving the provider runs ConfigureSampler + ConfigureOtlp (ApplyOtlp).
        sp.GetRequiredService<TracerProvider>().Should().NotBeNull();
    }

    [Fact]
    public void AddNodePilotTelemetry_MetricsWithPrometheusAndOtlp_BuildsMeterProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Env());

        services.AddNodePilotTelemetry(
            Config(new()
            {
                ["OpenTelemetry:Enabled"] = "true",
                ["OpenTelemetry:Exporters:Traces"] = "false",
                ["OpenTelemetry:Exporters:Metrics"] = "true",
                ["OpenTelemetry:Exporters:PrometheusScrape"] = "true",
                ["OpenTelemetry:Exporters:Logs"] = "false",
                ["OpenTelemetry:MetricExportIntervalSeconds"] = "5",
                ["OpenTelemetry:Otlp:Endpoint"] = "http://collector.example:4317",
                ["OpenTelemetry:Otlp:Protocol"] = "grpc",
            }),
            Env());

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<MeterProvider>().Should().NotBeNull();
    }

    [Fact]
    public void AddNodePilotTelemetry_LogsExporterEnabled_RegistersOtlpLogging()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Env());

        services.AddNodePilotTelemetry(
            Config(new()
            {
                ["OpenTelemetry:Enabled"] = "true",
                ["OpenTelemetry:Exporters:Traces"] = "false",
                ["OpenTelemetry:Exporters:Metrics"] = "false",
                ["OpenTelemetry:Exporters:Logs"] = "true",
                ["OpenTelemetry:Otlp:Endpoint"] = "http://collector.example:4317",
            }),
            Env());

        using var sp = services.BuildServiceProvider();
        // The logging pipeline builds a LoggerFactory without throwing.
        sp.GetRequiredService<ILoggerFactory>().Should().NotBeNull();
    }
}
