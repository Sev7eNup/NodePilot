namespace NodePilot.Cli.Settings;

public enum OutputFormat
{
    Table,
    Json,
    Yaml,
}

public static class OutputFormatParser
{
    public static OutputFormat Resolve(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested.Trim().ToLowerInvariant() switch
            {
                "table" => OutputFormat.Table,
                "json" => OutputFormat.Json,
                "yaml" or "yml" => OutputFormat.Yaml,
                _ => throw new ArgumentException($"Unknown output format '{requested}'. Use table | json | yaml."),
            };
        }

        return Console.IsOutputRedirected ? OutputFormat.Json : OutputFormat.Table;
    }
}
