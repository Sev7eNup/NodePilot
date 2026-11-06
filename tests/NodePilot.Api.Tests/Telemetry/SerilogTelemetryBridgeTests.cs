using System.Collections;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using NodePilot.Telemetry;
using Serilog;
using Xunit;

namespace NodePilot.Api.Tests.Telemetry;

/// <summary>
/// Verifies resource identity and actual log delivery through the configured OTLP transport.
/// </summary>
[Collection(SerilogTelemetryBridgeCollection.Name)]
public class SerilogTelemetryBridgeTests : IDisposable
{
    private readonly Dictionary<string, string?> _otelEnvironment;

    public SerilogTelemetryBridgeTests()
    {
        // The sink reads OTEL_* directly; these tests must never use an external collector.
        _otelEnvironment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Where(entry => ((string)entry.Key).StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value);
        foreach (var key in _otelEnvironment.Keys)
            Environment.SetEnvironmentVariable(key, null);
    }

    public void Dispose()
    {
        foreach (var (key, value) in _otelEnvironment)
            Environment.SetEnvironmentVariable(key, value);
    }

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

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AddNodePilotOpenTelemetry_Disabled_DoesNotExport(bool enabled, bool logsEnabled)
    {
        await using var collector = await OtlpTestCollector.StartAsync(HttpProtocols.Http1);
        var cfg = new LoggerConfiguration();
        var result = cfg.AddNodePilotOpenTelemetry(
            Config(new()
            {
                ["OpenTelemetry:Enabled"] = enabled.ToString(),
                ["OpenTelemetry:Exporters:Logs"] = logsEnabled.ToString(),
                ["OpenTelemetry:Otlp:Endpoint"] = collector.Endpoint,
                ["OpenTelemetry:Otlp:Protocol"] = "http/protobuf",
            }),
            Env());

        result.Should().BeSameAs(cfg);
        await EmitAndFlushAsync(result, $"disabled-{Guid.NewGuid():N}");

        collector.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AddNodePilotOpenTelemetry_GrpcDefault_ExportsLogRecord()
    {
        await using var collector = await OtlpTestCollector.StartAsync(HttpProtocols.Http2);
        var marker = $"grpc-{Guid.NewGuid():N}";
        var cfg = new LoggerConfiguration().AddNodePilotOpenTelemetry(
            Config(new()
            {
                ["OpenTelemetry:Enabled"] = "true",
                ["OpenTelemetry:Exporters:Logs"] = "true",
                ["OpenTelemetry:ServiceName"] = "nodepilot-test",
                ["OpenTelemetry:Environment"] = "staging",
                ["OpenTelemetry:Otlp:Endpoint"] = collector.Endpoint,
            }),
            Env());

        await EmitAndFlushAsync(cfg, marker);

        var request = await collector.WaitForRequestAsync();
        request.Method.Should().Be("POST");
        request.Path.Should().Be("/opentelemetry.proto.collector.logs.v1.LogsService/Export");
        request.Protocol.Should().Be("HTTP/2");
        request.ContentType.Should().StartWith("application/grpc");
        request.Body.Length.Should().BeGreaterThan(5);
        request.Body[0].Should().Be(0, "the gRPC frame must be uncompressed");
        BinaryPrimitives.ReadInt32BigEndian(request.Body.AsSpan(1, 4)).Should().Be(request.Body.Length - 5);
        LogBodies(request.Body[5..]).Should().ContainSingle().Which.Should().Be(marker);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("http/protobuf")]
    public async Task AddNodePilotOpenTelemetry_HttpProtocolWithHeaders_ExportsLogRecord(string protocol)
    {
        await using var collector = await OtlpTestCollector.StartAsync(HttpProtocols.Http1);
        var marker = $"http-{Guid.NewGuid():N}";
        var cfg = new LoggerConfiguration().AddNodePilotOpenTelemetry(
            Config(new()
            {
                ["OpenTelemetry:Enabled"] = "true",
                ["OpenTelemetry:Exporters:Logs"] = "true",
                ["OpenTelemetry:Otlp:Endpoint"] = collector.Endpoint,
                ["OpenTelemetry:Otlp:Protocol"] = protocol,
                ["OpenTelemetry:Otlp:Headers"] = " x-api-key = synthetic-key , malformed-no-equals, x-tenant = team=one ",
            }),
            Env());

        await EmitAndFlushAsync(cfg, marker);

        var request = await collector.WaitForRequestAsync();
        request.Method.Should().Be("POST");
        request.Path.Should().Be("/v1/logs");
        request.Protocol.Should().Be("HTTP/1.1");
        request.ContentType.Should().Be("application/x-protobuf");
        request.Headers.Should().Contain("x-api-key", "synthetic-key");
        request.Headers.Should().Contain("x-tenant", "team=one");
        request.Headers.Should().NotContainKey("malformed-no-equals");
        LogBodies(request.Body).Should().ContainSingle().Which.Should().Be(marker);
    }

    private static async Task EmitAndFlushAsync(LoggerConfiguration config, string marker)
    {
        var logger = config.CreateLogger();
        try
        {
            logger.Information("{Marker}", marker);
        }
        finally
        {
            await logger.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    private static IEnumerable<string> LogBodies(byte[] payload)
    {
        // ExportLogsServiceRequest.resource_logs -> scope_logs -> log_records -> body -> string_value.
        return MessageFields(payload, 1)
            .SelectMany(resource => MessageFields(resource, 2))
            .SelectMany(scope => MessageFields(scope, 2))
            .SelectMany(record => MessageFields(record, 5))
            .SelectMany(body => MessageFields(body, 1))
            .Select(Encoding.UTF8.GetString);
    }

    private static IEnumerable<byte[]> MessageFields(byte[] payload, int fieldNumber)
    {
        using var input = new CodedInputStream(payload);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag == WireFormat.MakeTag(fieldNumber, WireFormat.WireType.LengthDelimited))
                yield return input.ReadBytes().ToByteArray();
            else
                input.SkipLastField();
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerilogTelemetryBridgeCollection
{
    public const string Name = "Serilog telemetry environment";
}
