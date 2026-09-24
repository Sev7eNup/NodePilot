using System.Collections;
using System.Text;

namespace NodePilot.Engine.PowerShell;

/// <summary>
/// Environment for the PowerShell processes the engine starts. Opening the in-process SDK pool
/// rewrites this process's PSModulePath and puts the SDK's PowerShell 7 core modules first. A
/// Windows PowerShell 5.1 child that inherits it loads those manifests and loses New-Guid,
/// Get-FileHash and the other script functions of its own modules. Children therefore get the
/// module path a freshly started process would have.
/// </summary>
internal static class ChildProcessEnvironment
{
    internal const string ModulePathVariable = "PSModulePath";

    /// <summary>User and machine PSModulePath from the registry, or null when neither is set.</summary>
    internal static string? ModulePath()
    {
        var parts = new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .Select(target => Environment.GetEnvironmentVariable(ModulePathVariable, target))
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var joined = string.Join(';', parts);
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>Replaces the module path in a <see cref="System.Diagnostics.ProcessStartInfo"/> environment.</summary>
    internal static void Apply(IDictionary<string, string?> environment)
    {
        var modulePath = ModulePath();
        if (modulePath is null)
            environment.Remove(ModulePathVariable);
        else
            environment[ModulePathVariable] = modulePath;
    }

    /// <summary>
    /// The current environment with the module path replaced, as a CreateProcessW Unicode
    /// environment block: <c>NAME=value\0</c> entries sorted by name, closed by an extra <c>\0</c>.
    /// </summary>
    internal static string BuildBlock()
    {
        var variables = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = (string?)entry.Value;
        Apply(variables);

        var block = new StringBuilder();
        foreach (var (name, value) in variables)
            block.Append(name).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString();
    }
}
