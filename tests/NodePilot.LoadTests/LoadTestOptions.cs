namespace NodePilot.LoadTests;

public class LoadTestOptions
{
    public string ApiBaseUrl { get; set; } = "http://localhost:5000";
    public string Username { get; set; } = "loadtest";
    public string Password { get; set; } = "loadtest-password";
    public string Scenario { get; set; } = "soak";
    public SeedOptions Seed { get; set; } = new();
    public SoakOptions Soak { get; set; } = new();
    public SpikeOptions Spike { get; set; } = new();
    public RampOptions Ramp { get; set; } = new();
    public BurstOptions Burst { get; set; } = new();
    public SignalRObserverOptions SignalR { get; set; } = new();
}

public class SeedOptions
{
    /// <summary>How many copies of each template to seed. Different copies have different UUIDs, so they don't collide.</summary>
    public int CopiesPerTemplate { get; set; } = 3;
    /// <summary>If true, reuse workflows from a previous run (matches by name prefix). Default: seed fresh every run.</summary>
    public bool ReuseExisting { get; set; } = false;
    public int DeepSequentialDepth { get; set; } = 50;
    public int WideFanoutWidth { get; set; } = 30;
    public int SubWorkflowDepth { get; set; } = 3;
}

public class SoakOptions
{
    public int Rps { get; set; } = 10;
    public int DurationSeconds { get; set; } = 1800;
}

public class SpikeOptions
{
    public int PeakRps { get; set; } = 200;
    public int RampUpSeconds { get; set; } = 10;
    public int HoldSeconds { get; set; } = 60;
    public int RampDownSeconds { get; set; } = 10;
}

public class RampOptions
{
    public int StartRps { get; set; } = 1;
    public int EndRps { get; set; } = 500;
    public int DurationSeconds { get; set; } = 600;
}

public class BurstOptions
{
    public int Concurrency { get; set; } = 100;
    /// <summary>How long to wait for each execution to reach a terminal status (Succeeded/Failed/Cancelled) before timing out.</summary>
    public int TerminalTimeoutSeconds { get; set; } = 300;
}

public class SignalRObserverOptions
{
    public bool Enabled { get; set; } = false;
    /// <summary>If enabled, the observer will connect to /hubs/execution and subscribe to a random subset of executions, measuring broadcast latency.</summary>
    public double SampleRate { get; set; } = 0.1;
}
