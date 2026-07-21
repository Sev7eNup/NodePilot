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
