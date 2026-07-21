using System.Reflection;

namespace NodePilot.Mcp.Resources;

/// <summary>
/// Reads data embedded into the assembly at build time (see the &lt;EmbeddedResource&gt; items in
/// the csproj). These must be embedded — after <c>dotnet tool install</c> there is no docs/ or
/// Resources/ folder on disk next to the tool.
/// </summary>
public static class EmbeddedResources
{
    private static readonly Assembly Asm = typeof(EmbeddedResources).Assembly;

    public const string ActivityConfigReference = "NodePilot.Mcp.Resources.Embedded.activity-config-reference.json";
    public const string WorkflowStyleguide = "NodePilot.Mcp.Resources.Embedded.workflow-styleguide.md";

    public static string Read(string manifestName)
    {
        using var stream = Asm.GetManifestResourceStream(manifestName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{manifestName}' not found. Available: {string.Join(", ", Asm.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
