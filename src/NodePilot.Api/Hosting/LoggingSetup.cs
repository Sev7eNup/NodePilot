using NodePilot.Telemetry;
using NodePilot.Api.Diagnostics;
using NodePilot.Api.Logging;
using Serilog;
using Serilog.Configuration;
using NodePilot.Core.Telemetry;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Centralises Serilog configuration so <c>Program.cs</c> stays a thin composition-root.
/// Two phases:
/// <list type="number">
///   <item><see cref="BuildBootstrapLogger"/> — picks the configured file format
///   <i>before</i> the host logger replaces the static <c>Log.Logger</c>, so the rolling
///   file's first line matches what CMTrace / JSON parsers will auto-detect on it.</item>
///   <item><see cref="ConfigureHostLogging"/> — wires the full Serilog pipeline
///   through <c>UseSerilog</c>, including the OpenTelemetry sink and the EF-Core
///   dampening overrides.</item>
/// </list>
/// </summary>
internal static class LoggingSetup
{
    public const string SerilogFileOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}";

    /// <summary>
    /// Build the bootstrap configuration the same way <c>WebApplicationBuilder</c> would —
    /// base JSON, environment-specific override, runtime overrides (UI-managed), then env
    /// vars. Returned config is used by the bootstrap logger and a couple of pre-host
    /// options (ThreadPool prewarming etc.).
    /// <para>The runtime overrides file is included so the bootstrap logger picks the same
    /// <c>Logging:Format</c> the host logger will use after <c>UseSerilog</c> reloads —
    /// CMTrace.exe auto-detects file format from the first few lines, a divergent format
    /// in the bootstrap window blanks out the entire file's column view.</para>
    /// </summary>
    public static IConfiguration BuildBootstrapConfiguration()
    {
        var basePath = Directory.GetCurrentDirectory();
        var bootstrapEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";

        // CLI args are mirrored from the process command line so a `dotnet run
        // --Settings:RuntimeOverridesPath=…` invocation resolves the override file
        // identically here and in the host — without this the bootstrap logger
        // would silently disagree with the post-Build configuration on Logging:Format
        // and write a divergent first line into the rolling file.
        var commandLineArgs = Environment.GetCommandLineArgs();
        var cliArgs = commandLineArgs.Length > 1
            ? commandLineArgs.Skip(1).ToArray()
            : Array.Empty<string>();

        // First pass: read base + env-specific + envvars + cli so we can resolve
        // Settings:RuntimeOverridesPath if it's set as an EnvVar (typical in containers)
        // or a CLI arg (typical in tests / one-shot recoveries).
        var firstPass = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{bootstrapEnv}.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(cliArgs)
            .Build();

        var overridesPath = NodePilot.Api.Configuration.RuntimeOverridesSetup
            .ResolveOverridesPath(firstPass, basePath);

        return new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{bootstrapEnv}.json", optional: true)
            .AddJsonFile(overridesPath, optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(cliArgs)
            .Build();
    }

    /// <summary>
    /// Pre-load minimal configuration so the bootstrap logger picks the SAME file format
    /// the host logger will use after <c>UseSerilog</c> reloads. Without this, bootstrap
    /// hardcodes the plain-text output template and emits unstructured lines into the
    /// rolling file before the Reload fires — CMTrace.exe auto-detects the file format
    /// from the first few lines, so a single plain-text line at the top forces the entire
    /// file into plain-text mode and blanks out the Date/Time/Component/Thread columns
    /// for every subsequent SMS-trace line. JSON mode has the same auto-detection problem.
    /// </summary>
    public static Serilog.ILogger BuildBootstrapLogger(IConfiguration bootstrapConfig)
    {
        return new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.ApplyFormat(
                LogFormatters.Create(bootstrapConfig["Logging:Format"]),
                ResolveLogFilePath(bootstrapConfig, Directory.GetCurrentDirectory()),
                SerilogFileOutputTemplate,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 200L * 1024 * 1024)
            .CreateBootstrapLogger();
    }

    /// <summary>
    /// Allow the production installer to point writable-log files at a separate ProgramData
    /// directory without needing write access to the Install-Dir. Relative paths resolve
    /// against <paramref name="rootFolder"/> so dev behaviour is unchanged.
    /// </summary>
    public static string ResolveLogFilePath(IConfiguration cfg, string rootFolder)
    {
        var overridePath = cfg["Logging:File:Path"];
        if (string.IsNullOrWhiteSpace(overridePath))
            return Path.Combine(rootFolder, "logs", "nodepilot-.log");
        return Path.IsPathRooted(overridePath)
            ? overridePath
            : Path.Combine(rootFolder, overridePath);
    }

    /// <summary>
    /// Mirrors <see cref="ResolveLogFilePath"/> for the second sink (the support log).
    /// Defaults to sitting next to the main log: <c>{rootFolder}/logs/nodepilot-support-.log</c>.
    /// </summary>
    public static string ResolveSupportLogFilePath(IConfiguration cfg, string rootFolder)
    {
        var overridePath = cfg["Logging:SupportLog:Path"];
        if (string.IsNullOrWhiteSpace(overridePath))
            return Path.Combine(rootFolder, "logs", "nodepilot-support-.log");
        return Path.IsPathRooted(overridePath)
            ? overridePath
            : Path.Combine(rootFolder, overridePath);
    }

    /// <summary>
    /// Wires the full Serilog pipeline into the host. Reads <c>Serilog:*</c> from
    /// configuration, applies EF-Core dampening, attaches the OpenTelemetry sink (when
    /// enabled), and routes the rolling file through the configured format
    /// (<c>text</c> / <c>cmtrace</c> / <c>json</c>).
    /// </summary>
    public static void ConfigureHostLogging(IHostBuilder host)
    {
        host.UseSerilog((ctx, services, cfg) =>
        {
            var logPath = ResolveLogFilePath(ctx.Configuration, ctx.HostingEnvironment.ContentRootPath);
            var logDir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
            {
                try { Directory.CreateDirectory(logDir); } catch { /* serilog will log the error */ }
            }

            // Support log: a lightweight second sink that only writes events carrying the
            // LogContext property SupportLog=true. Sources: LogActivity (always), AuditWriter
            // (allow-listed actions + failures), WorkflowEngine lifecycle events, boot banner,
            // retention summaries.
            var supportEnabled = ctx.Configuration.GetValue("Logging:SupportLog:Enabled", true);
            var supportPath = ResolveSupportLogFilePath(ctx.Configuration, ctx.HostingEnvironment.ContentRootPath);
            var supportRetained = ctx.Configuration.GetValue<int?>("Logging:SupportLog:RetainedFileCountLimit") ?? 90;
            var supportSizeLimit = ctx.Configuration.GetValue<long?>("Logging:SupportLog:FileSizeLimitBytes") ?? 10L * 1024 * 1024;

            // Support-log DB projection: a third sub-sink behind the same filter. Writes the same
            // events, structured, into the SupportEvents table for the web viewer. The channel is
            // a singleton (registered in DI); a FlushService consumes it in batches. The sink
            // pushes non-blocking, so the hot logging path never waits on DB I/O.
            var supportDbEnabled = ctx.Configuration.GetValue("Logging:SupportLog:DbProjectionEnabled", true);
            var supportChannel = services.GetService<SupportEventChannel>();
            cfg
                .ReadFrom.Configuration(ctx.Configuration)
                .ReadFrom.Services(services)
                // The Microsoft.Extensions.Logging `Logging:LogLevel:*` overrides are NOT honoured
                // by Serilog when we replace the host logger via UseSerilog + ReadFrom.Configuration
                // (Serilog reads only its own `Serilog:*` section). Apply the EF Core dampening
                // directly so the rolling file sink doesn't drown in per-request SQL dumps — these
                // were the lines that pushed the CMTrace output past its ~4 KB parse limit and
                // knocked out the column view.
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Connection", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Infrastructure", Serilog.Events.LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.With<OtelTagEnricher>()
                .Enrich.WithProperty("service.name", ctx.Configuration["OpenTelemetry:ServiceName"] ?? TelemetryConstants.ServiceName)
                .Enrich.WithProperty("deployment.environment", ctx.Configuration["OpenTelemetry:Environment"] ?? ctx.HostingEnvironment.EnvironmentName)
                .WriteTo.Console()
                // Perf: wrap the file sink in Async. Serilog.Sinks.Async buffers log events in an
                // in-memory queue and writes them to disk from a dedicated thread — logger calls
                // on the hot path return immediately instead of waiting on file I/O. The cost is
                // losing the last few queued events (default buffer size 10k) on a crash, which is
                // acceptable for operations logs. Opt out via Logging:File:Async=false if strict
                // durability is required.
                .WriteTo.Conditional(
                    _ => ctx.Configuration.GetValue("Logging:File:Async", true),
                    sink => sink.Async(a => a.ApplyFormat(
                        LogFormatters.Create(ctx.Configuration["Logging:Format"]),
                        logPath, SerilogFileOutputTemplate,
                        ctx.Configuration.GetValue<int?>("Logging:File:RetainedFileCountLimit") ?? 7,
                        ctx.Configuration.GetValue<long?>("Logging:File:FileSizeLimitBytes") ?? 100L * 1024 * 1024)))
                .WriteTo.Conditional(
                    _ => !ctx.Configuration.GetValue("Logging:File:Async", true),
                    sink => sink.ApplyFormat(
                        LogFormatters.Create(ctx.Configuration["Logging:Format"]),
                        logPath, SerilogFileOutputTemplate,
                        ctx.Configuration.GetValue<int?>("Logging:File:RetainedFileCountLimit") ?? 7,
                        ctx.Configuration.GetValue<long?>("Logging:File:FileSizeLimitBytes") ?? 100L * 1024 * 1024))
                // Support-log sub-sink: filters on SupportLog=true, writes its own plain-text
                // file. Kept synchronous (no async wrap) — the volume is low enough that
                // synchronous file I/O is not a problem, and it guarantees the last lines are
                // on disk even after a crash.
                .WriteTo.Conditional(
                    _ => supportEnabled,
                    sink => sink.Logger(lc => lc
                        .Filter.ByIncludingOnly(le =>
                            le.Properties.TryGetValue("SupportLog", out var v)
                            && v is Serilog.Events.ScalarValue { Value: true })
                        .WriteTo.File(
                            new SupportLogFormatter(),
                            supportPath,
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: supportRetained,
                            fileSizeLimitBytes: supportSizeLimit,
                            rollOnFileSizeLimit: true,
                            shared: true,
                            flushToDiskInterval: TimeSpan.FromSeconds(1))))
                // DB projection: the same SupportLog=true filter, pushes every matching event
                // over a bounded channel to the SupportEventFlushService. The channel is a
                // singleton (see Program.cs); if it's missing from DI, this sink is simply
                // skipped — no crash if someone removes the service from DI.
                .WriteTo.Conditional(
                    _ => supportDbEnabled && supportChannel is not null,
                    sink => sink.Logger(lc => lc
                        .Filter.ByIncludingOnly(le =>
                            le.Properties.TryGetValue("SupportLog", out var v)
                            && v is Serilog.Events.ScalarValue { Value: true })
                        .WriteTo.Sink(new SupportEventDbSink(supportChannel!))))
                .AddNodePilotOpenTelemetry(ctx.Configuration, ctx.HostingEnvironment);
        });
    }
}
