namespace NodePilot.Api.Dtos;

public record ObservabilityConfigResponse(
    bool Enabled,
    string? TraceUiUrlTemplate,
    string? TraceBackendName,
    bool PrometheusAvailable,
    string? BrowserOtlpEndpoint = null,
    string? ServiceName = null,
    string? Environment = null,
    string? GrafanaBaseUrl = null);

public record TelemetryPanel(
    string Key,
    string Title,
    string Unit,
    double? Value,
    string? Error);

public record TelemetrySummaryResponse(
    bool Available,
    List<TelemetryPanel> Panels);

/// <summary>Curated, role-safe content for one native metrics dashboard.</summary>
public record MetricsDashboardResponse(
    bool Available,
    string Key,
    string Title,
    List<TelemetryPanel> Panels,
    List<MetricsSeries> Series,
    List<MetricsTable> Tables,
    List<MetricsWidget>? Widgets = null);

public record MetricsSeries(string Key, string Title, string Unit, List<MetricsSeriesLine> Lines);
public record MetricsSeriesLine(string Label, List<MetricsPoint> Points);
public record MetricsPoint(long Timestamp, double? Value);
public record MetricsTable(string Key, string Title, string Unit, List<MetricsTableRow> Rows);
public record MetricsTableRow(string Label, double Value);

public record MetricsWidget(
    int Id,
    string Title,
    string? Description,
    string Type,
    string Unit,
    MetricsGridPosition Grid,
    List<MetricsDataSeries> Data,
    string? Error);
public record MetricsGridPosition(int X, int Y, int Width, int Height);
public record MetricsDataSeries(string Label, Dictionary<string, string> Labels, List<MetricsPoint> Points);
