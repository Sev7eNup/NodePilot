namespace NodePilot.Cli.Commands.Workflow;

/// <summary>
/// Parses repeated <c>--params key=value</c> CLI arguments into the dictionary the
/// execute endpoint expects. Extracted from <see cref="WorkflowRunCommand"/> so the
/// edge cases (empty key, missing '=', '=' in value) are unit-testable without
/// driving a full Spectre command pipeline.
/// </summary>
public static class RunParameterParser
{
    public static Dictionary<string, string>? Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return null;
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in args)
        {
            if (string.IsNullOrEmpty(raw))
                throw new ArgumentException("Empty --params value.");
            var idx = raw.IndexOf('=');
            if (idx <= 0)
                throw new ArgumentException($"--params Werte müssen 'key=value' sein, war: '{raw}'");
            var key = raw[..idx];
            var value = raw[(idx + 1)..]; // intentionally keeps any '=' that appear later
            dict[key] = value;
        }
        return dict;
    }
}
