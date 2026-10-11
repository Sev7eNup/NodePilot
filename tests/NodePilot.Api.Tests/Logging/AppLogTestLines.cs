using NodePilot.Api.Hosting;
using NodePilot.Api.Logging;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Display;
using Serilog.Parsing;

namespace NodePilot.Api.Tests.Logging;

/// <summary>Produces application-log lines with the formatters the API really uses.</summary>
internal static class AppLogTestLines
{
    public const string DefaultSource = "NodePilot.Data.Probe";

    public static LogEvent Event(
        LogEventLevel level,
        string template,
        DateTimeOffset timestamp,
        Exception? exception = null,
        string source = DefaultSource,
        params (string Name, object? Value)[] properties)
    {
        var props = properties
            .Select(p => new LogEventProperty(p.Name, new ScalarValue(p.Value)))
            .Append(new LogEventProperty("SourceContext", new ScalarValue(source)));
        return new LogEvent(timestamp, level, exception, new MessageTemplateParser().Parse(template), props);
    }

    public static string[] CmTrace(LogEvent e) => Lines(new CmTraceFormatter(), e);

    public static string[] Clef(LogEvent e) => Lines(new CompactJsonFormatter(), e);

    public static string[] Ecs(LogEvent e) => Lines(new EcsJsonFormatter(), e);

    public static string[] Text(LogEvent e)
        => Lines(new MessageTemplateTextFormatter(LoggingSetup.SerilogFileOutputTemplate), e);

    public static Exception Thrown(string message)
    {
        try { throw new InvalidOperationException(message); }
        catch (InvalidOperationException ex) { return ex; }
    }

    private static string[] Lines(ITextFormatter formatter, LogEvent e)
    {
        using var writer = new StringWriter();
        formatter.Format(e, writer);
        return writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();
    }
}
