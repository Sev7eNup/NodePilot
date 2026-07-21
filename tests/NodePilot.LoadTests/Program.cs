using Microsoft.Extensions.Configuration;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;
using NodePilot.LoadTests;
using NodePilot.LoadTests.Scenarios;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.loadtests.json", optional: false)
    .AddEnvironmentVariables(prefix: "LOADTEST__")
    .AddCommandLine(args, new Dictionary<string, string>
    {
        ["--scenario"] = "Scenario",
        ["--rps"] = "Soak:Rps",
        ["--duration"] = "Soak:DurationSeconds",
        ["--concurrency"] = "Burst:Concurrency",
    })
    .Build();

var options = new LoadTestOptions();
config.Bind(options);

Console.WriteLine($"NodePilot LoadTests — scenario={options.Scenario}, target={options.ApiBaseUrl}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// --- Auth + seed ---------------------------------------------------------
var client = new NodePilotApiClient(options.ApiBaseUrl);
Console.WriteLine("Logging in...");
await client.LoginAsync(options.Username, options.Password, cts.Token);

Console.WriteLine("Seeding workflows...");
var seed = await Seeder.SeedAsync(client, options, cts.Token);
var allWorkflowIds = seed.PlainWorkflowIds.Concat(seed.SubWorkflowRootIds).ToList();
Console.WriteLine($"Seeded {allWorkflowIds.Count} workflows ({seed.PlainWorkflowIds.Count} plain + {seed.SubWorkflowRootIds.Count} sub-roots).");

// --- Optional SignalR observer -------------------------------------------
SignalRObserver? observer = null;
if (options.SignalR.Enabled)
{
    observer = new SignalRObserver(options.ApiBaseUrl, client.Token!, options.SignalR.SampleRate);
    await observer.StartAsync(cts.Token);
    Console.WriteLine($"SignalR observer attached (sample rate {options.SignalR.SampleRate:P0}).");
}

// --- Build scenario ------------------------------------------------------
var terminalTimeout = TimeSpan.FromSeconds(options.Burst.TerminalTimeoutSeconds);
var simulations = BuildSimulations(options);
var scenario = ExecutionScenarioFactory.Build(
    name: $"nodepilot-{options.Scenario}",
    client: client,
    workflowIds: allWorkflowIds,
    terminalTimeout: terminalTimeout,
    observer: observer,
    simulations.ToArray());

var stats = NBomberRunner
    .RegisterScenarios(scenario)
    .WithReportFileName($"loadtest-{options.Scenario}-{DateTime.UtcNow:yyyyMMdd-HHmmss}")
    .WithReportFolder("reports")
    .WithReportFormats(ReportFormat.Html, ReportFormat.Txt, ReportFormat.Md)
    .Run();

// --- Post-run: gates + observer summary ----------------------------------
var scenarioStats = stats.ScenarioStats[0];
var success = scenarioStats.Ok.Request.Count;
var failed = scenarioStats.Fail.Request.Count;
var total = success + failed;
var successRate = total > 0 ? (double)success / total : 0.0;

Console.WriteLine();
Console.WriteLine($"=== {options.Scenario} results ===");
Console.WriteLine($"  total iterations:     {total}");
Console.WriteLine($"  success rate:         {successRate:P2}");
Console.WriteLine($"  p50 latency:          {scenarioStats.Ok.Latency.Percent50:F1} ms");
Console.WriteLine($"  p95 latency:          {scenarioStats.Ok.Latency.Percent95:F1} ms");
Console.WriteLine($"  p99 latency:          {scenarioStats.Ok.Latency.Percent99:F1} ms");

if (observer is not null)
{
    var s = observer.Snapshot();
    Console.WriteLine($"  SignalR samples:      {s.Samples}");
    Console.WriteLine($"  SignalR p50/95/99 ms: {s.P50Ms:F1} / {s.P95Ms:F1} / {s.P99Ms:F1}");
    await observer.DisposeAsync();
}

var runningAfter = await client.CountRunningExecutionsAsync(cts.Token);
Console.WriteLine($"  running-after-run:    {runningAfter} (non-zero indicates leaking executions)");

// --- Exit code for CI / gates -------------------------------------------
int exitCode = 0;
if (successRate < 0.995) { Console.WriteLine("GATE FAILED: success rate < 99.5%"); exitCode = 1; }
if (runningAfter > 0) { Console.WriteLine("GATE FAILED: executions still running after scenario ended"); exitCode = 1; }
return exitCode;


static List<LoadSimulation> BuildSimulations(LoadTestOptions opts)
{
    return opts.Scenario.ToLowerInvariant() switch
    {
        "soak" => new()
        {
            Simulation.Inject(
                rate: opts.Soak.Rps,
                interval: TimeSpan.FromSeconds(1),
                during: TimeSpan.FromSeconds(opts.Soak.DurationSeconds))
        },
        "spike" => new()
        {
            Simulation.RampingInject(
                rate: opts.Spike.PeakRps,
                interval: TimeSpan.FromSeconds(1),
                during: TimeSpan.FromSeconds(opts.Spike.RampUpSeconds)),
            Simulation.Inject(
                rate: opts.Spike.PeakRps,
                interval: TimeSpan.FromSeconds(1),
                during: TimeSpan.FromSeconds(opts.Spike.HoldSeconds)),
            Simulation.RampingInject(
                rate: 0,
                interval: TimeSpan.FromSeconds(1),
                during: TimeSpan.FromSeconds(opts.Spike.RampDownSeconds))
        },
        "ramp" => new()
        {
            Simulation.RampingInject(
                rate: opts.Ramp.EndRps,
                interval: TimeSpan.FromSeconds(1),
                during: TimeSpan.FromSeconds(opts.Ramp.DurationSeconds))
        },
        "burst" => new()
        {
            // All requests launched in 1 second, then wait for them to drain within the terminal timeout.
            Simulation.Inject(
                rate: opts.Burst.Concurrency,
                interval: TimeSpan.FromSeconds(1),
                during: TimeSpan.FromSeconds(1)),
            Simulation.KeepConstant(
                copies: 0,
                during: TimeSpan.FromSeconds(opts.Burst.TerminalTimeoutSeconds))
        },
        _ => throw new ArgumentException($"Unknown scenario '{opts.Scenario}'. Use soak | spike | ramp | burst.")
    };
}
