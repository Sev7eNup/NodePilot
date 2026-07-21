using System.Text.RegularExpressions;
using FluentAssertions;
using NodePilot.Api.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace NodePilot.Api.Tests.Logging;

public class CmTraceFormatterTests
{
    // Matches the canonical CMTrace/SMS-trace wrapper the formatter must emit. CMTrace.exe
    // parses against this exact attribute ordering; if the regex fails the viewer shows
    // empty Date/Time/Component/Thread columns for the row.
    private static readonly Regex SmsLine = new(
        @"^<!\[LOG\[(?<msg>.*)\]LOG\]!>" +
        @"<time=""(?<time>\d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{3})"" " +
        @"date=""(?<date>\d{2}-\d{2}-\d{4})"" " +
        @"component=""(?<component>[^""]*)"" " +
        @"context=""(?<context>[^""]*)"" " +
        @"type=""(?<type>[123])"" " +
        @"thread=""(?<thread>\d+)"" " +
        @"file=""(?<file>[^""]*)"">$",
        RegexOptions.Compiled);

    private static string FormatOne(LogEvent ev)
    {
        var sut = new CmTraceFormatter();
        using var sw = new StringWriter();
        sut.Format(ev, sw);
        // WriteLine terminates the entry with Environment.NewLine — strip it for regex match.
        return sw.ToString().TrimEnd('\r', '\n');
    }

    private static LogEvent MakeEvent(
        LogEventLevel level = LogEventLevel.Information,
        string template = "hello",
        Exception? ex = null,
        params LogEventProperty[] extraProps)
    {
        var parsed = new MessageTemplateParser().Parse(template);
        return new LogEvent(
            DateTimeOffset.Parse("2026-04-23T14:23:01.456+02:00"),
            level, ex, parsed, extraProps);
    }

    private static LogEventProperty Prop(string name, object value) =>
        new(name, new ScalarValue(value));

    [Fact]
    public void Format_RegularInfo_ProducesParseableSmsLine()
    {
        var line = FormatOne(MakeEvent(LogEventLevel.Information, "Workflow started"));

        var m = SmsLine.Match(line);
        m.Success.Should().BeTrue($"line was: {line}");
        m.Groups["msg"].Value.Should().StartWith("Workflow started");
        m.Groups["type"].Value.Should().Be("1");
        m.Groups["date"].Value.Should().Be("04-23-2026");
    }

    [Fact]
    public void Format_Warning_EmitsType2()
    {
        var line = FormatOne(MakeEvent(LogEventLevel.Warning, "almost bad"));
        SmsLine.Match(line).Groups["type"].Value.Should().Be("2");
    }

    [Theory]
    [InlineData(LogEventLevel.Error)]
    [InlineData(LogEventLevel.Fatal)]
    public void Format_ErrorOrFatal_EmitsType3(LogEventLevel level)
    {
        var line = FormatOne(MakeEvent(level, "boom"));
        SmsLine.Match(line).Groups["type"].Value.Should().Be("3");
    }

    [Fact]
    public void Format_MessageWithCrLf_FlattensToSingleLine()
    {
        // EF Core "Executed DbCommand" logs the raw CommandText which contains literal
        // newlines. If those leak through the formatter, the file would show a line per
        // SQL clause and CMTrace can't parse the subsequent lines as entries.
        var ev = MakeEvent(LogEventLevel.Information,
            "SELECT *\r\nFROM users\r\nWHERE id = 1");

        var line = FormatOne(ev);

        line.Should().NotContain("\r").And.NotContain("\n");
        SmsLine.IsMatch(line).Should().BeTrue($"line was: {line}");
        var msg = SmsLine.Match(line).Groups["msg"].Value;
        msg.Should().Contain("SELECT * FROM users WHERE id = 1");
    }

    [Fact]
    public void Format_MessageWithLogTerminatorLiteral_IsSanitized()
    {
        // A user-supplied payload (script output echoing another log line back) could
        // carry the exact `]LOG]!>` sequence. Without sanitising, the SMS wrapper would
        // terminate inside the message body and break parsing.
        var ev = MakeEvent(LogEventLevel.Information, "payload: ]LOG]!> rest");

        var line = FormatOne(ev);

        // Exactly one terminator — the one at the real end of the message body.
        Regex.Count(line, Regex.Escape("]LOG]!>")).Should().Be(1);
        SmsLine.IsMatch(line).Should().BeTrue($"line was: {line}");
    }

    [Fact]
    public void Format_ComponentWithDoubleQuotes_IsSanitized()
    {
        var ev = MakeEvent(
            LogEventLevel.Information, "hi",
            extraProps: new[] { Prop("SourceContext", "Weird\"Name") });

        var line = FormatOne(ev);

        var m = SmsLine.Match(line);
        m.Success.Should().BeTrue($"line was: {line}");
        m.Groups["component"].Value.Should().NotContain("\"").And.Contain("Weird'Name");
    }

    [Fact]
    public void Format_VeryLongMessage_IsCappedBelow4096()
    {
        // CMTrace.exe falls back to raw rendering once a line exceeds ~4096 chars (KB 2716956).
        // The formatter trims the message body so the full assembled line stays comfortably under.
        var ev = MakeEvent(LogEventLevel.Information, new string('x', 5000));

        var line = FormatOne(ev);

        line.Length.Should().BeLessThan(4096);
        SmsLine.IsMatch(line).Should().BeTrue($"line was: {line}");
        line.Should().Contain("truncated +");
    }

    [Fact]
    public void Format_TimeOffset_UsesMinutesNotColon()
    {
        // CMTrace wants the bias as ±<minutes> glued to the ms — "+02:00" breaks parsing.
        var line = FormatOne(MakeEvent());
        SmsLine.Match(line).Groups["time"].Value.Should().Be("14:23:01.456+120");
    }

    [Fact]
    public void Format_NullSourceContext_FallsBackToNodePilot()
    {
        var line = FormatOne(MakeEvent());
        SmsLine.Match(line).Groups["component"].Value.Should().Be("NodePilot");
    }

    [Fact]
    public void Format_SourceContext_AppearsAsComponentNotInMessageBody()
    {
        // SourceContext is routed to the component= attribute and must NOT also be dumped
        // into the message body (would be redundant noise and could contain chars that
        // disrupt later parsing).
        var ev = MakeEvent(
            LogEventLevel.Information, "hi",
            extraProps: new[] { Prop("SourceContext", "My.Namespace.Worker") });

        var line = FormatOne(ev);

        var m = SmsLine.Match(line);
        m.Groups["component"].Value.Should().Be("My.Namespace.Worker");
        m.Groups["msg"].Value.Should().NotContain("SourceContext=");
    }

    [Fact]
    public void Format_ExceptionWithStackTrace_IsFlattenedInlineWithMessage()
    {
        Exception caught;
        try { throw new InvalidOperationException("the cause"); }
        catch (InvalidOperationException ex) { caught = ex; }

        var ev = MakeEvent(LogEventLevel.Error, "op failed", ex: caught);

        var line = FormatOne(ev);

        line.Should().NotContain("\n");
        SmsLine.IsMatch(line).Should().BeTrue($"line was: {line}");
        var msg = SmsLine.Match(line).Groups["msg"].Value;
        msg.Should().Contain("op failed").And.Contain("Exception:").And.Contain("InvalidOperationException");
    }
}
