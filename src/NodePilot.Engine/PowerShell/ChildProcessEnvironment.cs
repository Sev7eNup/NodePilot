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
        => Combine(
            Environment.GetEnvironmentVariable(ModulePathVariable, EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable(ModulePathVariable, EnvironmentVariableTarget.Machine));

    /// <summary>
    /// The module path rule: blank parts are dropped, the rest joined as <c>user;machine</c>, null
    /// when nothing is left. <see cref="PowerShellFunction"/> implements the same rule in PowerShell.
    /// </summary>
    internal static string? Combine(string? user, string? machine)
    {
        var parts = new[] { user, machine }.Where(value => !string.IsNullOrWhiteSpace(value));
        var joined = string.Join(';', parts);
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>
    /// <see cref="Combine"/> as a PowerShell function, for generated scripts that start a process
    /// themselves. Runs on Windows PowerShell 5.1 too, because the same script also runs remotely,
    /// where it reads the target machine's registry.
    /// </summary>
    internal const string PowerShellFunction = """
        function __npChildModulePath([string]$user, [string]$machine) {
            $parts = @($user, $machine) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            if ($parts) { $parts -join ';' } else { $null }
        }
        """;

    /// <summary>
    /// Script lines that set the module path on the ProcessStartInfo in <paramref name="psiVariable"/>
    /// (e.g. <c>$psi</c>). Needs <see cref="PowerShellFunction"/> and UseShellExecute = false.
    /// </summary>
    internal static string PowerShellApply(string psiVariable) => $$"""
        $__npModulePath = __npChildModulePath ([Environment]::GetEnvironmentVariable('{{ModulePathVariable}}', 'User')) ([Environment]::GetEnvironmentVariable('{{ModulePathVariable}}', 'Machine'))
        if ($null -ne $__npModulePath) { {{psiVariable}}.EnvironmentVariables['{{ModulePathVariable}}'] = $__npModulePath }
        else { {{psiVariable}}.EnvironmentVariables.Remove('{{ModulePathVariable}}') }
        """;

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
