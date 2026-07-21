using System.Text.RegularExpressions;

namespace NodePilot.Core.Activities;

/// <summary>
/// Shared validation for a custom activity definition's metadata + parameter schema. Lives in Core
/// so the API controller, the CLI and the MCP server all enforce the same rules. Returns the first
/// error message, or null when valid.
/// </summary>
public static partial class CustomActivityValidation
{
    // Parameter names become PowerShell variables, so they must match the wrapper's allow-list
    // (ParameterKeyValidator: [A-Za-z0-9_]+) — stricter than the Key slug, which permits hyphens.
    [GeneratedRegex(@"^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ParamNameRegex();

    // Material Symbol names are lowercase snake_case; a light guard (full-set validation is impractical).
    [GeneratedRegex(@"^[a-z0-9_]{1,60}$", RegexOptions.CultureInvariant)]
    private static partial Regex IconRegex();

    private static readonly IReadOnlySet<string> AllowedEngines =
        new HashSet<string>(StringComparer.Ordinal) { "auto", "pwsh", "powershell" };

    // Names that collide with wrapper-injected / reserved PowerShell variables or the forced exitCode.
    private static readonly IReadOnlySet<string> ReservedParamNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Params", "exitCode", "input", "args", "PSScriptRoot", "PSCommandPath",
            "ErrorActionPreference", "ProgressPreference", "_",
        };

    public static string? Validate(
        string? key,
        string name,
        string icon,
        string engine,
        IReadOnlyList<CustomActivityInputParameter> inputs,
        IReadOnlyList<CustomActivityOutputParameter> outputs,
        bool requireKey)
    {
        if (requireKey)
        {
            if (string.IsNullOrWhiteSpace(key) || !CustomActivityType.IsValidKey(key))
                return "Key must match [A-Za-z0-9_-]{1,64}.";
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            return "Name is required and must be at most 200 characters.";

        if (string.IsNullOrWhiteSpace(icon) || !IconRegex().IsMatch(icon))
            return "Icon must be a Material Symbol name ([a-z0-9_], max 60).";

        if (!AllowedEngines.Contains(engine))
            return "Engine must be one of: auto, pwsh, powershell.";

        var inputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in inputs)
        {
            if (string.IsNullOrWhiteSpace(p.Name) || !ParamNameRegex().IsMatch(p.Name))
                return $"Input parameter name '{p.Name}' must match [A-Za-z0-9_]+.";
            if (ReservedParamNames.Contains(p.Name))
                return $"Input parameter name '{p.Name}' is reserved.";
            if (!CustomActivityParameterTypes.Input.Contains(p.Type))
                return $"Input parameter '{p.Name}' has unsupported type '{p.Type}'.";
            if (string.IsNullOrWhiteSpace(p.Label))
                return $"Input parameter '{p.Name}' needs a label.";
            if (!inputNames.Add(p.Name))
                return $"Duplicate input parameter name '{p.Name}'.";
        }

        var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in outputs)
        {
            if (string.IsNullOrWhiteSpace(o.Name) || !ParamNameRegex().IsMatch(o.Name))
                return $"Output parameter name '{o.Name}' must match [A-Za-z0-9_]+.";
            if (ReservedParamNames.Contains(o.Name))
                return $"Output parameter name '{o.Name}' is reserved (exitCode is always provided automatically).";
            if (!CustomActivityParameterTypes.Output.Contains(o.Type))
                return $"Output parameter '{o.Name}' has unsupported type '{o.Type}'.";
            if (!outputNames.Add(o.Name))
                return $"Duplicate output parameter name '{o.Name}'.";
            // Disjoint from inputs: a same-named output could otherwise surface a never-overwritten
            // input value despite the capture allow-list.
            if (inputNames.Contains(o.Name))
                return $"Parameter name '{o.Name}' is used for both an input and an output; names must be disjoint.";
        }

        return null;
    }
}
