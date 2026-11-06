using System.Management.Automation;
using NodePilot.Core.Execution;

namespace NodePilot.Remote;

/// <summary>Drains SDK collections as records arrive, retaining only bounded text.</summary>
public sealed class PowerShellOutputCapture : IDisposable
{
    public ScriptOutputBudget Budget { get; }
    public ScriptOutputBudget.Buffer Output { get; }
    public ScriptOutputBudget.Buffer Errors { get; }
    public ScriptOutputBudget.Buffer Warnings { get; }
    public ScriptOutputBudget.Buffer Verbose { get; }

    public PowerShellOutputCapture(PowerShell ps, PSDataCollection<PSObject> results, int limit)
    {
        Budget = new ScriptOutputBudget(limit);
        Output = Budget.CreateBuffer();
        Errors = Budget.CreateBuffer();
        Warnings = Budget.CreateBuffer();
        Verbose = Budget.CreateBuffer();
        Attach(results, Output);
        Attach(ps.Streams.Error, Errors);
        Attach(ps.Streams.Warning, Warnings);
        Attach(ps.Streams.Verbose, Verbose);
        Attach(ps.Streams.Debug, Budget.CreateBuffer());
        Attach(ps.Streams.Information, Budget.CreateBuffer());
        Attach(ps.Streams.Progress, Budget.CreateBuffer());
    }

    private void Attach<T>(PSDataCollection<T> stream, ScriptOutputBudget.Buffer buffer)
    {
        stream.DataAdded += (_, _) =>
        {
            foreach (var value in stream.ReadAll())
                if (!Budget.Exceeded) buffer.AppendLine(SafeToString(value));
        };
    }

    private static string SafeToString(object? value)
    {
        if (value is null) return string.Empty;
        if (value is PSObject ps)
        {
            var baseObj = ps.BaseObject;
            if (baseObj is string s) return s;
            // A property bag (a custom object, or any object deserialized from a target) has an
            // empty ToString of its own; the PSObject renders its properties or its remote text.
            if (baseObj is not PSCustomObject)
                try { return baseObj?.ToString() ?? string.Empty; } catch { }
            try { return ps.ToString() ?? string.Empty; } catch { }
            return $"<{baseObj?.GetType().Name ?? "PSObject"}: ToString failed>";
        }
        try { return value.ToString() ?? string.Empty; }
        catch { return $"<{value.GetType().Name}: ToString failed>"; }
    }

    public void Dispose() => Budget.Dispose();
}
