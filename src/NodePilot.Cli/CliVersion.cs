using System.Reflection;

namespace NodePilot.Cli;

/// <summary>
/// The product version, taken from the assembly the SDK stamped from
/// <c>Directory.Build.props</c>. Exists so <c>np --version</c> cannot drift away from the
/// version the release build ships — it used to be a literal in Program.cs and reported 1.0.0
/// after the product had already moved on.
/// </summary>
public static class CliVersion
{
    /// <summary>
    /// e.g. <c>1.0.1</c>. The SDK appends <c>+&lt;commit-sha&gt;</c> to the informational version
    /// (SourceLink); that suffix is noise in <c>--version</c> output and is cut here.
    /// </summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var informational = typeof(CliVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
            return typeof(CliVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
