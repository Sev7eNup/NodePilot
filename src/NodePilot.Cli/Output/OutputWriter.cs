using System.Text.Json;
using NodePilot.Cli.Api;
using NodePilot.Cli.Settings;
using Spectre.Console;

namespace NodePilot.Cli.Output;

/// <summary>
/// Centralised output sink. Commands hand it a value plus a TableRenderer; the writer
/// dispatches to JSON/YAML/table based on caller intent + TTY detection. Stderr is
/// reserved for log-style messages so `np … -o json | jq` stays clean.
/// </summary>
public sealed class OutputWriter
{
    private readonly IAnsiConsole _stdout;
    private readonly IAnsiConsole _stderr;
    private readonly OutputFormat _format;

    public OutputFormat Format => _format;

    public OutputWriter(OutputFormat format, bool noColor)
    {
        var colors = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect;
        _stdout = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Console.Out),
            ColorSystem = colors,
        });
        _stderr = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Console.Error),
            ColorSystem = colors,
        });
        _format = format;
    }

    public IAnsiConsole Out => _stdout;

    public void WriteData<T>(T value, Action<IAnsiConsole, T> renderTable)
    {
        switch (_format)
        {
            case OutputFormat.Json:
                Console.Out.Write(JsonSerializer.Serialize(value, NodePilotApiClient.JsonOptions));
                Console.Out.WriteLine();
                break;
            case OutputFormat.Yaml:
                Console.Out.Write(YamlEmitter.Emit(value));
                break;
            default:
                renderTable(_stdout, value);
                break;
        }
    }

    public void Info(string markup) => _stderr.MarkupLine(markup);
    public void Warning(string markup) => _stderr.MarkupLine($"[yellow]{markup}[/]");
    public void Error(string markup) => _stderr.MarkupLine($"[red]{markup}[/]");

    /// <summary>
    /// Writes a multi-line diagnostic that was not built as markup — certificate subjects and
    /// exception messages carry characters Spectre would parse. Escaped once, here, so no caller
    /// has to remember it per field.
    /// </summary>
    public void ErrorBlock(string plainText) => _stderr.MarkupLine($"[red]{Markup.Escape(plainText)}[/]");
    public void Success(string markup) => _stderr.MarkupLine($"[green]{markup}[/]");
}
