using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Stats;

// `np observability query` / `query_range` — thin wrappers around the Prometheus
// proxy endpoints. The server returns the raw Prometheus JSON (200 or upstream
// status preserved); we forward it to the user as pretty-printed JSON. Table
// rendering is intentional only for instant queries with a `vector` result — for
// matrices we always fall back to JSON because that's the only sensible shape
// for a CLI to display.

public class ObservabilityQuerySettings : GlobalSettings
{
    [CommandOption("--query <PROMQL>")]
    [Description("PromQL expression. Subject to the server's metric-name allow-list (OpenTelemetry:AllowedMetricPrefixes).")]
    public string? Query { get; set; }

    [CommandOption("--time <UNIX-SECONDS>")]
    [Description("Optional evaluation time (Unix seconds). Default: server's now().")]
    public long? Time { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class ObservabilityQueryCommand : BaseCommand<ObservabilityQuerySettings>
{
    public ObservabilityQueryCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, ObservabilityQuerySettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Query)) { writer.Error("--query <PROMQL> ist Pflicht."); return ExitCodes.Error; }
        var api = ClientFactory.Create(session);
        using var doc = await api.ObservabilityQueryAsync(settings.Query, settings.Time, ct);
        RenderPromResult(writer, doc);
        return ExitCodes.Success;
    }

    /// <summary>
    /// For instant queries (resultType=vector) we can render a clean table; for everything
    /// else (matrix, scalar, string) we dump the JSON. Servicing a CLI operator: a vector
    /// is the common case (single sample per series), a matrix happens only on query_range.
    /// </summary>
    internal static void RenderPromResult(OutputWriter writer, JsonDocument doc)
    {
        // Non-table formats: route through the format-aware printer so `-o yaml` gets YAML
        // instead of silently falling through to JSON.
        if (writer.Format != OutputFormat.Table)
        {
            Settings.JsonShapedPrint.Write(writer, doc);
            return;
        }

        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("resultType", out var rt)
            || !data.TryGetProperty("result", out var result))
        {
            // Body doesn't look like the Prometheus envelope (e.g. an upstream error). Fall
            // back to the pretty-JSON dump so the operator can still see what came back.
            Settings.JsonShapedPrint.Write(writer, doc);
            return;
        }

        var resultType = rt.GetString();
        if (resultType == "vector")
        {
            var t = new Table().Border(TableBorder.Rounded).AddColumn("Labels").AddColumn("Value").AddColumn("At");
            foreach (var row in result.EnumerateArray())
            {
                var labels = row.TryGetProperty("metric", out var m) ? RenderLabels(m) : "{}";
                string val = "-", at = "-";
                if (row.TryGetProperty("value", out var pair) && pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2)
                {
                    if (pair[0].TryGetDouble(out var ts)) at = DateTimeOffset.FromUnixTimeMilliseconds((long)(ts * 1000)).ToLocalTime().ToString("u");
                    val = pair[1].GetString() ?? "-";
                }
                t.AddRow(Markup.Escape(labels), Markup.Escape(val), Markup.Escape(at));
            }
            writer.Out.Write(t);
            return;
        }

        // matrix / scalar / string → dump pretty JSON; table form would be misleading.
        Settings.JsonShapedPrint.Write(writer, doc);
    }

    private static string RenderLabels(JsonElement metric)
    {
        var pairs = new List<string>();
        foreach (var prop in metric.EnumerateObject())
            pairs.Add($"{prop.Name}=\"{prop.Value.GetString()}\"");
        return "{" + string.Join(", ", pairs) + "}";
    }
}

public sealed class ObservabilityQueryRangeSettings : ObservabilityQuerySettings
{
    [CommandOption("--start <UNIX-SECONDS>")]
    [Description("Range start (Unix seconds).")]
    public long Start { get; set; }

    [CommandOption("--end <UNIX-SECONDS>")]
    [Description("Range end (Unix seconds).")]
    public long End { get; set; }

    [CommandOption("--step <DURATION>")]
    [Description("Step width — Prometheus-style (e.g. `15s`, `1m`, `5m`).")]
    public string? Step { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class ObservabilityQueryRangeCommand : BaseCommand<ObservabilityQueryRangeSettings>
{
    public ObservabilityQueryRangeCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, ObservabilityQueryRangeSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Query)) { writer.Error("--query <PROMQL> ist Pflicht."); return ExitCodes.Error; }
        if (settings.Start <= 0 || settings.End <= 0) { writer.Error("--start und --end (Unix-Sekunden) sind Pflicht."); return ExitCodes.Error; }
        if (string.IsNullOrWhiteSpace(settings.Step)) { writer.Error("--step ist Pflicht (z.B. `15s`)."); return ExitCodes.Error; }
        var api = ClientFactory.Create(session);
        using var doc = await api.ObservabilityQueryRangeAsync(settings.Query, settings.Start, settings.End, settings.Step, ct);
        ObservabilityQueryCommand.RenderPromResult(writer, doc);
        return ExitCodes.Success;
    }
}
