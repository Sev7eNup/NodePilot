using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Configuration;
using Serilog.Sinks.OpenTelemetry;
using NodePilot.Core.Telemetry;

namespace NodePilot.Telemetry;

/// <summary>
/// Helpers to wire the Serilog OpenTelemetry sink from the host, so logs carry the
/// same resource attributes as the .NET OTel SDK pipeline and are correlated by
/// <c>TraceId</c>/<c>SpanId</c>.
/// </summary>
public static class SerilogTelemetryBridge
{
    /// <summary>
    /// Appends the OpenTelemetry sink to the given Serilog configuration if telemetry
    /// is enabled and log export is active. Safe to call unconditionally.
    /// </summary>
    public static LoggerConfiguration AddNodePilotOpenTelemetry(
        this LoggerConfiguration cfg,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = new NodePilotTelemetryOptions();
        configuration.GetSection("OpenTelemetry").Bind(options);

        if (!options.Enabled || !options.Exporters.Logs)
        {
            return cfg;
        }

        var endpoint = string.IsNullOrWhiteSpace(options.Otlp.Endpoint) ? "http://localhost:4317" : options.Otlp.Endpoint;
        var protocol = string.Equals(options.Otlp.Protocol, "http", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.Otlp.Protocol, "http/protobuf", StringComparison.OrdinalIgnoreCase)
                ? OtlpProtocol.HttpProtobuf
                : OtlpProtocol.Grpc;

        var serviceName = string.IsNullOrWhiteSpace(options.ServiceName) ? TelemetryConstants.ServiceName : options.ServiceName;
        var deploymentEnv = options.Environment ?? environment.EnvironmentName;

        return cfg.WriteTo.OpenTelemetry(o =>
        {
            o.Endpoint = endpoint;
            o.Protocol = protocol;
            o.ResourceAttributes = new Dictionary<string, object>
            {
                ["service.name"] = serviceName,
                ["deployment.environment"] = deploymentEnv,
                ["host.name"] = System.Environment.MachineName,
                ["nodepilot.node.role"] = "api",
            };

            if (!string.IsNullOrWhiteSpace(options.Otlp.Headers))
            {
                foreach (var pair in options.Otlp.Headers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var idx = pair.IndexOf('=');
                    if (idx > 0)
                    {
                        o.Headers[pair[..idx].Trim()] = pair[(idx + 1)..].Trim();
                    }
                }
            }
        });
    }
}
