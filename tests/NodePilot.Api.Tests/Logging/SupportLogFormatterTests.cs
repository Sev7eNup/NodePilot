using FluentAssertions;
using NodePilot.Api.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace NodePilot.Api.Tests.Logging;

/// <summary>
/// Pins the plain-text format of the support-log sub-sink. Operators typically scan the
/// file with Notepad++/grep — if the format tips over (e.g. a JSON body instead of plain
/// text), human readability is immediately lost.
/// </summary>
public class SupportLogFormatterTests
{
    private static string FormatOne(LogEvent ev)
    {
        var sut = new SupportLogFormatter();
        using var sw = new StringWriter();
        sut.Format(ev, sw);
        return sw.ToString();
    }

    private static LogEvent MakeEvent(
        LogEventLevel level = LogEventLevel.Information,
        string template = "EXECUTION_STARTED workflow=DailyReport exec=8a7b1234",
        Exception? ex = null)
    {
        var parsed = new MessageTemplateParser().Parse(template);
        return new LogEvent(
            DateTimeOffset.Parse("2026-05-15T14:23:11.412+02:00"),
            level, ex, parsed, Array.Empty<LogEventProperty>());
    }

    [Fact]
    public void Format_Info_ProducesTimestampLevelMessage()
    {
        var output = FormatOne(MakeEvent(LogEventLevel.Information));

        output.Should().StartWith("2026-05-15 14:23:11.412 [INFO] ");
        output.Should().Contain("EXECUTION_STARTED workflow=DailyReport");
        output.Should().EndWith(Environment.NewLine);
    }

    [Fact]
    public void Format_Warning_UsesWarnLabel()
    {
        var output = FormatOne(MakeEvent(LogEventLevel.Warning, "STEP_FAILED exec=abc"));
        output.Should().Contain("[WARN]");
        output.Should().Contain("STEP_FAILED");
    }

    [Fact]
    public void Format_Error_UsesErrLabel()
    {
        var output = FormatOne(MakeEvent(LogEventLevel.Error, "boom"));
        output.Should().Contain("[ERR ]");
    }

    [Fact]
    public void Format_WithException_AppendsExceptionMessageWithoutStackTrace()
    {
        var ex = new InvalidOperationException("connection refused");
        var output = FormatOne(MakeEvent(LogEventLevel.Error, "STEP_FAILED reason=…", ex));

        output.Should().Contain("STEP_FAILED");
        output.Should().Contain("connection refused");
        // The stack trace deliberately belongs in the main log, not the support log.
        output.Should().NotContain("InvalidOperationException");
        output.Should().NotContain("   at ");
    }

    [Fact]
    public void Format_DoesNotEmitProperties()
    {
        // Properties like trace_id are useful for correlation in the main log, but in the
        // support log they would distract from the human-readable message. The formatter
        // must not append them.
        var parsed = new MessageTemplateParser().Parse("hello");
        var ev = new LogEvent(
            DateTimeOffset.Now,
            LogEventLevel.Information, null, parsed,
            new[] { new LogEventProperty("trace_id", new ScalarValue("abc123")) });
        var output = FormatOne(ev);

        output.Should().NotContain("trace_id");
        output.Should().NotContain("abc123");
    }
}
