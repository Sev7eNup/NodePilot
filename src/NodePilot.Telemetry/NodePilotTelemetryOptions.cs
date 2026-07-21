namespace NodePilot.Telemetry;

public sealed class NodePilotTelemetryOptions
{
    public bool Enabled { get; set; } = false;
    public string? ServiceName { get; set; }
    public string? Environment { get; set; }
    public bool RedactHostnames { get; set; } = false;
    public int MetricExportIntervalSeconds { get; set; } = 30;
    public OtlpOptions Otlp { get; set; } = new();
    public SamplingOptions Sampling { get; set; } = new();
    public ExporterToggles Exporters { get; set; } = new();
    public TraceUiOptions TraceUi { get; set; } = new();
    public PrometheusOptions Prometheus { get; set; } = new();
    /// <summary>Optional public base URL for the Grafana instance used by native dashboard drill-down links.</summary>
    public string? GrafanaBaseUrl { get; set; }

    /// <summary>
    /// Metric-name prefixes allowed through the <c>/api/observability/query</c> and
    /// <c>/query_range</c> proxy endpoints. When null/empty a safe built-in default is
    /// used ("nodepilot_", "http_server_", "process_", "dotnet_", "up"). Configure via
    /// <c>OpenTelemetry:AllowedMetricPrefixes</c> when exposing custom metric families.
    /// </summary>
    public string[]? AllowedMetricPrefixes { get; set; }

    public sealed class OtlpOptions
    {
        public string? Endpoint { get; set; } = "http://localhost:4317";
        public string? Protocol { get; set; } = "grpc";
        public string? Headers { get; set; }

        /// <summary>
        /// HTTP endpoint that browsers POST traces to (OTLP/HTTP, <c>/v1/traces</c>).
        /// When set, the SPA initializes the OpenTelemetry Web SDK and forwards spans
        /// directly to the collector. The operator must enable CORS on the collector.
        /// Typical value: <c>http://localhost:4318/v1/traces</c>.
        /// </summary>
        public string? BrowserEndpoint { get; set; }
    }

    public sealed class SamplingOptions
    {
        public string Mode { get; set; } = "ParentBasedTraceIdRatio";
        public double Ratio { get; set; } = 1.0;
    }

    public sealed class ExporterToggles
    {
        public bool Traces { get; set; } = true;
        public bool Metrics { get; set; } = true;
        public bool Logs { get; set; } = true;
        public bool PrometheusScrape { get; set; } = false;
    }

    /// <summary>
    /// UI deep-link to a trace in the observability backend.
    /// Template may contain the placeholder <c>{traceId}</c>.
    /// </summary>
    public sealed class TraceUiOptions
    {
        public string? UrlTemplate { get; set; }
        public string? BackendName { get; set; } = "Tempo";
    }

    /// <summary>
    /// Optional server-side proxy to a Prometheus-compatible HTTP query API.
    /// When configured, the UI dashboard can switch to "Telemetry Mode" and pull
    /// aggregates from Prometheus instead of the local database.
    /// </summary>
    public sealed class PrometheusOptions
    {
        public string? QueryEndpoint { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? BearerToken { get; set; }
        public int TimeoutSeconds { get; set; } = 10;
    }
}
