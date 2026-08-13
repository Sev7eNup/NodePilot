using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using FluentAssertions;
using NodePilot.Telemetry;
using Serilog;
using Xunit;

namespace NodePilot.Api.Tests.Telemetry;

/// <summary>
/// Covers the Serilog → OpenTelemetry sink bridge. This wiring feeds the ECS/SIEM log
/// pipeline, so a mistake here (wrong protocol switch, header parsing that throws on a
/// malformed pair) silently drops production logs. Exercise every branch: the disabled
/// no-op, the gRPC/HTTP protocol split, and the comma-separated header parser including a
/// malformed entry that must be skipped rather than crash the host on boot.
/// </summary>
public class SerilogTelemetryBridgeTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IHostEnvironment Env(string name = "Production") => new StubEnv { EnvironmentName = name };

    private sealed class StubEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "NodePilot.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public void CreateResourceAttributes_RedactionEnabled_RemovesHostnameAndUsesProcessStableRandomInstanceId()
    {
        const string sensitiveHostname = "NODEPILOT-SENSITIVE-HOST";
        var options = new NodePilotTelemetryOptions
        {
            RedactHostnames = true,
            ServiceName = "nodepilot-test",
            Environment = "staging",
        };
        var firstAttributes = SerilogTelemetryBridge.CreateResourceAttributes(
            options,
            Env(),
            sensitiveHostname,
            processId: 1234);
        var secondAttributes = SerilogTelemetryBridge.CreateResourceAttributes(
            options,
            Env(),
            "A-DIFFERENT-SENSITIVE-HOST",
            processId: 5678);

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
    public void CreateResourceAttributes_RedactionDisabled_PreservesExistingHostnameAttributes()
    {
        const string hostname = "NODEPILOT-LEGACY-HOST";
        var options = new NodePilotTelemetryOptions
        {
            RedactHostnames = false,
            ServiceName = "nodepilot-test",
            Environment = "staging",
        };

        var attributes = SerilogTelemetryBridge.CreateResourceAttributes(
            options,
            Env(),
            hostname,
            processId: 4321);

        attributes.Should().BeEquivalentTo(new Dictionary<string, object>
        {
            ["service.name"] = "nodepilot-test",
            ["deployment.environment"] = "staging",
            ["host.name"] = hostname,
            ["nodepilot.node.role"] = "api",
        });
        attributes.Should().NotContainKey("service.instance.id");
    }

    [Fact]
    public void AddNodePilotOpenTelemetry_Disabled_ReturnsConfigUnchangedAndLogsWithoutOtel()
    {
        var cfg = new LoggerConfiguration();
        var result = cfg.AddNodePilotOpenTelemetry(
            Config(new() { ["OpenTelemetry:Enabled"] = "false" }), Env());

        result.Should().BeSameAs(cfg);
        using var logger = result.CreateLogger();
        logger.Information("no otel sink attached");
    }

    [Fact]
    public void AddNodePilotOpenTelemetry_EnabledButLogsExporterOff_IsNoOp()
    {
        var cfg = new LoggerConfiguration();
        var result = cfg.AddNodePilotOpenTelemetry(
            Config(new()
            {
                ["OpenTelemetry:Enabled"] = "true",
                ["OpenTelemetry:Exporters:Logs"] = "false",
            }),
            Env());

        result.Should().BeSameAs(cfg);
    }

    [Fact]
    public void AddNodePilotOpenTelemetry_GrpcDefault_BuildsWorkingLogger()
    {
        var cfg = new LoggerConfiguration().AddNodePilotOpenTelemetry(
            Config(new()
            {
                ["OpenTelemetry:Enabled"] = "true",
                ["OpenTelemetry:Exporters:Logs"] = "true",
                ["OpenTelemetry:ServiceName"] = "nodepilot-test",
                ["OpenTelemetry:Environment"] = "staging",
                // no protocol → grpc default, no endpoint → localhost:4317 default
            }),
            Env());

        using var logger = cfg.CreateLogger();
        logger.Information("emitted via otel grpc sink");
    }

    [Theory]
    [InlineData("http")]
    [InlineData("http/protobuf")]
    public void AddNodePilotOpenTelemetry_HttpProtocolWithHeaders_ParsesHeadersAndBuilds(string protocol)
    {
        var cfg = new LoggerConfiguration().AddNodePilotOpenTelemetry(
            Config(new()
            {
                ["OpenTelemetry:Enabled"] = "true",
                ["OpenTelemetry:Exporters:Logs"] = "true",
                ["OpenTelemetry:Otlp:Endpoint"] = "http://collector.example:4318",
                ["OpenTelemetry:Otlp:Protocol"] = protocol,
                // one well-formed header, one malformed pair with no '=' that must be skipped
                ["OpenTelemetry:Otlp:Headers"] = "x-api-key=secret123 , malformed-no-equals",
            }),
            Env());

        using var logger = cfg.CreateLogger();
        logger.Information("emitted via otel http sink");
    }
}
