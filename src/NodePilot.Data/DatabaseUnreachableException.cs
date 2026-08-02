namespace NodePilot.Data;

/// <summary>
/// Thrown at boot when the configured database cannot be reached, so the migration bootstrap
/// could not run. Distinct from a migration that fails against a reachable database: this one
/// means "nothing is listening / we cannot log in", and its message names the connection target
/// so the first line of the console output is actionable rather than a provider stack trace.
/// </summary>
public sealed class DatabaseUnreachableException : Exception
{
    public DatabaseUnreachableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
