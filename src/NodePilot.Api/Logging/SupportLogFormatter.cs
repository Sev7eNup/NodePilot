using Serilog.Events;
using Serilog.Formatting;

namespace NodePilot.Api.Logging;

/// <summary>
/// Fixed plain-text formatter for the second Serilog sink (the Support Log).
/// One line per event: <c>{Timestamp} [{Level:u4}] {RenderedMessage}</c>, with an optional
/// second line for <c>Exception.Message</c> (no stack trace — that lives in the main log
/// under the same <c>trace_id</c>). Properties are deliberately not appended: the Support
/// Log is meant for human eyes, not structured-log tooling.
/// </summary>
internal sealed class SupportLogFormatter : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        output.Write(logEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        output.Write(" [");
        output.Write(FormatLevel(logEvent.Level));
        output.Write("] ");
        output.WriteLine(logEvent.RenderMessage());

        if (logEvent.Exception is { } ex)
        {
            output.Write("        ");
            output.WriteLine(ex.Message);
        }
    }

    private static string FormatLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose     => "TRCE",
        LogEventLevel.Debug       => "DBUG",
        LogEventLevel.Information => "INFO",
        LogEventLevel.Warning     => "WARN",
        LogEventLevel.Error       => "ERR ",
        LogEventLevel.Fatal       => "FATL",
        _                         => "INFO",
    };
}
