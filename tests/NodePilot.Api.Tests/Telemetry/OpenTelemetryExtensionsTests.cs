using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using NodePilot.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
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
    [Fact]
    public void Options_DefaultToHostnameRedaction()
    {
        new NodePilotTelemetryOptions().RedactHostnames.Should().BeTrue();
    }

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
    public void CreateResourceBuilder_RedactionEnabled_RemovesHostnameAndUsesProcessStableRandomInstanceId()
    {
        const string sensitiveHostname = "NODEPILOT-SENSITIVE-HOST";
        var options = new NodePilotTelemetryOptions
        {
            RedactHostnames = true,
            ServiceName = "nodepilot-test",
            Environment = "staging",
        };
        var firstResource = OpenTelemetryExtensions.CreateResourceBuilder(
            options,
            Env(),
            sensitiveHostname,
            processId: 1234,
            baseResourceBuilder: ResourceBuilder.CreateEmpty().AddAttributes(new Dictionary<string, object>
            {
                ["host.name"] = "ENVIRONMENT-INJECTED-HOST",
            })).Build();
        var secondResource = OpenTelemetryExtensions.CreateResourceBuilder(
            options,
            Env(),
            "A-DIFFERENT-SENSITIVE-HOST",
            processId: 5678).Build();

        var firstAttributes = firstResource.Attributes.ToDictionary(pair => pair.Key, pair => pair.Value);
        var secondAttributes = secondResource.Attributes.ToDictionary(pair => pair.Key, pair => pair.Value);

        firstAttributes.Should().NotContainKey("host.name");
        secondAttributes.Should().NotContainKey("host.name");
        var serviceInstanceId = firstAttributes["service.instance.id"].Should().BeOfType<string>().Subject;
        var secondServiceInstanceId = secondAttributes["service.instance.id"].Should().BeOfType<string>().Subject;
        serviceInstanceId.ToLowerInvariant().Should().NotContain(sensitiveHostname.ToLowerInvariant());
        serviceInstanceId.ToLowerInvariant().Should().NotContain("a-different-sensitive-host");
        serviceInstanceId.Should().MatchRegex("^nodepilot-[0-9a-f]{32}$");
        serviceInstanceId.Should().Be(secondServiceInstanceId);
    }

    [Fact]
    public void CreateResourceBuilder_RedactionDisabled_PreservesHostnameAndProcessInstanceId()
    {
        const string hostname = "NODEPILOT-LEGACY-HOST";
        var options = new NodePilotTelemetryOptions
        {
            RedactHostnames = false,
            ServiceName = "nodepilot-test",
        };

        var resource = OpenTelemetryExtensions.CreateResourceBuilder(
            options,
            Env(),
            hostname,
            processId: 4321).Build();
        var attributes = resource.Attributes.ToDictionary(pair => pair.Key, pair => pair.Value);

        attributes["host.name"].Should().Be(hostname);
        attributes["service.instance.id"].Should().Be($"{hostname}:4321");
    }

    [Fact]
    public void AddNodePilotTelemetry_RedactionEnabled_ProviderResourceOmitsMachineName()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Env());
        services.AddNodePilotTelemetry(
            Config(new()
            {
                ["OpenTelemetry:Enabled"] = "true",
                ["OpenTelemetry:RedactHostnames"] = "true",
                ["OpenTelemetry:Exporters:Traces"] = "true",
                ["OpenTelemetry:Exporters:Metrics"] = "false",
                ["OpenTelemetry:Exporters:Logs"] = "false",
            }),
            Env());

        using var serviceProvider = services.BuildServiceProvider();
        var resourceAttributes = serviceProvider.GetRequiredService<TracerProvider>()
            .GetResource()
            .Attributes
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        resourceAttributes.Should().NotContainKey("host.name");
        resourceAttributes["service.instance.id"].Should().BeOfType<string>()
            .Which.ToLowerInvariant().Should().NotContain(System.Environment.MachineName.ToLowerInvariant());
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
