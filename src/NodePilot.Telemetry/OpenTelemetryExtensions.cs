using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using NodePilot.Core.Telemetry;

namespace NodePilot.Telemetry;

public static class OpenTelemetryExtensions
{
    public static IServiceCollection AddNodePilotTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = new NodePilotTelemetryOptions();
        configuration.GetSection("OpenTelemetry").Bind(options);
        services.TryAddSingleton(options);

        // Mirror the singleton via the IOptions(Monitor) pipeline so consumers like
        // AdminSettingsController, which read `IOptionsMonitor<NodePilotTelemetryOptions>`
        // to populate the Settings UI, see the live config instead of a default-constructed
        // instance with `Enabled = false`. Without this, the UI's OTel toggle reads as
        // false even when /metrics is exporting and Prometheus is scraping happily.
        services.Configure<NodePilotTelemetryOptions>(configuration.GetSection("OpenTelemetry"));

        // PrometheusClient is always registered so the ObservabilityController can
        // respond (with a 503 from its IsConfigured guard) even when OTel is disabled.
        services.AddHttpClient<PrometheusClient>();

        if (!options.Enabled)
        {
            return services;
        }

        var serviceName = string.IsNullOrWhiteSpace(options.ServiceName) ? TelemetryConstants.ServiceName : options.ServiceName;
        var serviceVersion = typeof(OpenTelemetryExtensions).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var serviceInstanceId = $"{System.Environment.MachineName}:{System.Environment.ProcessId}";
        var deploymentEnv = options.Environment ?? environment.EnvironmentName;

        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(serviceName, serviceVersion: serviceVersion, serviceInstanceId: serviceInstanceId)
            .AddAttributes(new[]
            {
                new KeyValuePair<string, object>("deployment.environment", deploymentEnv),
                new KeyValuePair<string, object>("host.name", System.Environment.MachineName),
                new KeyValuePair<string, object>("host.os.type", "windows"),
                new KeyValuePair<string, object>("nodepilot.node.role", "api"),
            });

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(rb => rb.AddService(serviceName, serviceVersion: serviceVersion, serviceInstanceId: serviceInstanceId));

        if (options.Exporters.Traces)
        {
            otel.WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resourceBuilder)
                    .AddSource(TelemetryConstants.Sources.Engine)
                    .AddSource(TelemetryConstants.Sources.EngineActivities)
                    .AddSource(TelemetryConstants.Sources.Remote)
                    .AddSource(TelemetryConstants.Sources.Scheduler)
                    .AddSource(TelemetryConstants.Sources.Api)
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        o.RecordException = true;
                        o.Filter = ctx =>
                        {
                            var path = ctx.Request.Path.Value;
                            if (string.IsNullOrEmpty(path)) return true;
                            if (path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase)) return false;
                            if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)) return false;
                            return true;
                        };
                    })
                    .AddHttpClientInstrumentation(o =>
                    {
                        o.RecordException = true;
                    })
                    .AddEntityFrameworkCoreInstrumentation(o =>
                    {
                        // EFCore tracer redaction notes (instrumentation is still beta-only
                        // as of 2026-05; package version pinned in NodePilot.Telemetry.csproj).
                        //
                        // Verified via reflection against 1.15.1-beta.1, the *defaults* are
                        // already aligned with our audit posture:
                        //   - SetDbQueryParameters = false  → parameter VALUES are NOT
                        //                                     emitted as `db.query.parameter.*`
                        //                                     tags. This is the redaction
                        //                                     control we care about.
                        //   - EmitOldAttributes    = true   → legacy `db.statement` carries
                        //                                     the parameterised SQL text
                        //                                     (column / table names + @p0
                        //                                     placeholders). No row-level
                        //                                     data leaks through this path
                        //                                     because EFCore parameterises
                        //                                     properly.
                        //   - EmitNewAttributes    = false  → new `db.query.text` schema
                        //                                     opt-in only.
                        //
                        // The properties themselves are *internal* on this package (Filter
                        // + EnrichWithIDbCommand are the only public knobs), so we cannot
                        // assert these values from this configuration callback. If a future
                        // beta flips SetDbQueryParameters to true by default, the
                        // EnrichWithIDbCommand fall-back below strips any parameter-value
                        // tags before the span is exported — defense in depth.
                        o.EnrichWithIDbCommand = (activity, _) =>
                        {
                            // Walk the tag list; any tag whose key starts with
                            // "db.query.parameter." carries a parameter value and must not
                            // leave this process. SetTag(key, null) removes the tag
                            // (.NET 5+ Activity behaviour).
                            List<string>? toClear = null;
                            foreach (var (key, _) in activity.Tags)
                            {
                                if (key.StartsWith("db.query.parameter.", StringComparison.Ordinal))
                                {
                                    toClear ??= new List<string>(2);
                                    toClear.Add(key);
                                }
                            }
                            if (toClear is null) return;
                            foreach (var key in toClear) activity.SetTag(key, null);
                        };
                    });

                ConfigureSampler(tracing, options);
                ConfigureOtlp(tracing, options);
            });
        }

        if (options.Exporters.Metrics)
        {
            otel.WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(resourceBuilder)
                    .AddMeter(TelemetryConstants.Meters.Engine)
                    .AddMeter(TelemetryConstants.Meters.Remote)
                    .AddMeter(TelemetryConstants.Meters.Scheduler)
                    .AddMeter(TelemetryConstants.Meters.Api)
                    .AddMeter(TelemetryConstants.Meters.Data)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddProcessInstrumentation();

                if (options.Exporters.PrometheusScrape)
                {
                    metrics.AddPrometheusExporter();
                }

                ConfigureOtlpMetrics(metrics, options);
            });
        }

        if (options.Exporters.Logs)
        {
            services.AddLogging(logging =>
            {
                logging.AddOpenTelemetry(o =>
                {
                    o.SetResourceBuilder(resourceBuilder);
                    o.IncludeFormattedMessage = true;
                    o.IncludeScopes = true;
                    o.ParseStateValues = true;
                    ConfigureOtlpLogs(o, options);
                });
            });
        }

        return services;
    }

    private static void ConfigureSampler(TracerProviderBuilder tracing, NodePilotTelemetryOptions options)
    {
        var ratio = Math.Clamp(options.Sampling.Ratio, 0.0, 1.0);
        var sampler = options.Sampling.Mode switch
        {
            "AlwaysOn" => (Sampler)new AlwaysOnSampler(),
            "AlwaysOff" => new AlwaysOffSampler(),
            "TraceIdRatio" => new TraceIdRatioBasedSampler(ratio),
            _ => new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio)),
        };
        tracing.SetSampler(sampler);
    }

    private static void ConfigureOtlp(TracerProviderBuilder tracing, NodePilotTelemetryOptions options)
    {
        tracing.AddOtlpExporter(o => ApplyOtlp(o, options));
    }

    private static void ConfigureOtlpMetrics(MeterProviderBuilder metrics, NodePilotTelemetryOptions options)
    {
        metrics.AddOtlpExporter((exporterOptions, readerOptions) =>
        {
            ApplyOtlp(exporterOptions, options);
            readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                Math.Max(1, options.MetricExportIntervalSeconds) * 1000;
        });
    }

    private static void ConfigureOtlpLogs(OpenTelemetryLoggerOptions logging, NodePilotTelemetryOptions options)
    {
        logging.AddOtlpExporter(o => ApplyOtlp(o, options));
    }

    private static void ApplyOtlp(
        OpenTelemetry.Exporter.OtlpExporterOptions exporterOptions,
        NodePilotTelemetryOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Otlp.Endpoint))
        {
            exporterOptions.Endpoint = new Uri(options.Otlp.Endpoint);
        }

        exporterOptions.Protocol = options.Otlp.Protocol?.ToLowerInvariant() switch
        {
            "http" or "http/protobuf" => OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf,
            _ => OpenTelemetry.Exporter.OtlpExportProtocol.Grpc,
        };

        if (!string.IsNullOrWhiteSpace(options.Otlp.Headers))
        {
            exporterOptions.Headers = options.Otlp.Headers;
        }
    }
}
