using FluentAssertions;
using NodePilot.Api.Diagnostics;
using Serilog.Events;
using Xunit;
using static NodePilot.Api.Tests.Logging.AppLogTestLines;

namespace NodePilot.Api.Tests.Logging;

public sealed class AppLogParserTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 12, 14, 23, 1, 456, TimeSpan.FromHours(2));

    [Fact]
    public void Parse_CmTrace_RoundTripsTimeLevelSourceMessageAndProperties()
    {
        var workflowId = Guid.NewGuid();
        var line = CmTrace(Event(LogEventLevel.Warning, "Database {Host} answered slowly", Stamp,
            properties: [("Host", "db1"), ("WorkflowId", workflowId)]));

        var entry = AppLogParser.Parse(line).Entries.Should().ContainSingle().Subject;

        entry.Timestamp.Should().Be(Stamp);
        entry.Timestamp.Offset.Should().Be(TimeSpan.FromHours(2));
        entry.Level.Should().Be(AppLogLevel.Warning);
        entry.Source.Should().Be(DefaultSource);
        entry.Message.Should().Be("Database \"db1\" answered slowly");
        entry.Details.Should().Contain($"WorkflowId={workflowId}").And.NotContain("SourceContext");
        entry.Format.Should().Be(AppLogParser.FormatCmTrace);
        entry.Truncated.Should().BeFalse();
    }

    [Theory]
    [InlineData(LogEventLevel.Error, "Error")]
    [InlineData(LogEventLevel.Fatal, "Error")]
    [InlineData(LogEventLevel.Information, "Information")]
    [InlineData(LogEventLevel.Debug, "Information")]
    [InlineData(LogEventLevel.Verbose, "Information")]
    public void Parse_CmTrace_LevelFollowsWhatTheFormatterKeeps(LogEventLevel written, string expected)
    {
        var line = CmTrace(Event(written, "something", Stamp));

        AppLogParser.Parse(line).Entries.Single().Level.ToString().Should().Be(expected);
    }

    [Fact]
    public void Parse_CmTrace_ExceptionLandsInDetailsNotInMessage()
    {
        var line = CmTrace(Event(LogEventLevel.Error, "Login failed for {User}", Stamp,
            Thrown("ldap bind refused"), properties: [("User", "svc")]));

        line.Should().ContainSingle("cmtrace writes one physical line per event");
        var entry = AppLogParser.Parse(line).Entries.Single();

        entry.Message.Should().Be("Login failed for \"svc\"");
        entry.Details.Should().Contain("InvalidOperationException").And.Contain("ldap bind refused");
    }

    [Fact]
    public void Parse_CmTrace_CappedMessageIsFlaggedTruncated()
    {
        var line = CmTrace(Event(LogEventLevel.Warning, "payload {Body}", Stamp,
            properties: [("Body", new string('x', 5000))]));

        var entry = AppLogParser.Parse(line).Entries.Single();

        entry.Truncated.Should().BeTrue();
        entry.Message.Should().NotContain("[truncated").And.StartWith("payload \"xxx");
    }

    [Fact]
    public void Parse_CmTrace_NeutralisedTerminatorInMessageStillParses()
    {
        var line = CmTrace(Event(LogEventLevel.Information, "copied {Text}", Stamp,
            properties: [("Text", "a ]LOG]!> b")]));

        var entry = AppLogParser.Parse(line).Entries.Should().ContainSingle().Subject;

        entry.Message.Should().Be("copied \"a ]LOG]_> b\"");
    }

    [Fact]
    public void Parse_CmTrace_QuoteInComponentDoesNotBreakTheLine()
    {
        var line = CmTrace(Event(LogEventLevel.Information, "hello", Stamp, source: "Odd\"Name"));

        AppLogParser.Parse(line).Entries.Single().Source.Should().Be("Odd'Name");
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose, "Verbose")]
    [InlineData(LogEventLevel.Debug, "Debug")]
    [InlineData(LogEventLevel.Information, "Information")]
    [InlineData(LogEventLevel.Warning, "Warning")]
    [InlineData(LogEventLevel.Error, "Error")]
    [InlineData(LogEventLevel.Fatal, "Fatal")]
    public void Parse_Text_KeepsAllSixLevels(LogEventLevel written, string expected)
    {
        var lines = Text(Event(written, "plain event", Stamp));

        AppLogParser.Parse(lines, TimeZoneInfo.Utc).Entries.Single().Level.ToString().Should().Be(expected);
    }

    [Fact]
    public void Parse_Text_ReadsSourceFromPropertiesAndJoinsExceptionLines()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus3", TimeSpan.FromHours(3), "plus3", "plus3");
        var lines = Text(Event(LogEventLevel.Error, "Send to {Host} failed", Stamp,
            Thrown("smtp down"), "NodePilot.Engine.Mail", ("Host", "mail1")));
        lines.Length.Should().BeGreaterThan(1, "the text format puts the exception on following lines");

        var entry = AppLogParser.Parse(lines, zone).Entries.Should().ContainSingle().Subject;

        entry.Source.Should().Be("NodePilot.Engine.Mail");
        entry.Message.Should().Be("Send to mail1 failed");
        entry.Details.Should().Contain("smtp down");
        // The text format has no offset: its clock time is read in the supplied server zone.
        entry.Timestamp.Should().Be(new DateTimeOffset(2026, 10, 12, 14, 23, 1, 456, TimeSpan.FromHours(3)));
        entry.Format.Should().Be(AppLogParser.FormatText);
    }

    [Fact]
    public void Parse_Text_BracesInTheMessageDoNotHideTheProperties()
    {
        var lines = Text(Event(LogEventLevel.Warning, "Got {Json}", Stamp,
            source: "NodePilot.Api.Probe", properties: [("Json", "{\"a\":1}")]));

        var entry = AppLogParser.Parse(lines, TimeZoneInfo.Utc).Entries.Single();

        entry.Message.Should().Be("Got {\"a\":1}");
        entry.Source.Should().Be("NodePilot.Api.Probe");
    }

    [Fact]
    public void Parse_Clef_RendersTheTemplateAndKeepsIt()
    {
        var line = Clef(Event(LogEventLevel.Error, "Step {StepId} failed after {Attempts} tries", Stamp,
            Thrown("boom"), properties: [("StepId", "abc"), ("Attempts", 3)]));

        var entry = AppLogParser.Parse(line).Entries.Should().ContainSingle().Subject;

        entry.Message.Should().Be("Step abc failed after 3 tries");
        entry.Template.Should().Be("Step {StepId} failed after {Attempts} tries");
        entry.Level.Should().Be(AppLogLevel.Error);
        entry.Source.Should().Be(DefaultSource);
        entry.Details.Should().Contain("boom");
        entry.Timestamp.UtcDateTime.Should().Be(Stamp.UtcDateTime);
        entry.Format.Should().Be(AppLogParser.FormatJson);
    }

    [Fact]
    public void Parse_Clef_MissingLevelMeansInformation_AndFatalSurvives()
    {
        var info = Clef(Event(LogEventLevel.Information, "ok", Stamp));
        var fatal = Clef(Event(LogEventLevel.Fatal, "dead", Stamp));

        info.Single().Should().NotContain("\"@l\"");
        AppLogParser.Parse(info).Entries.Single().Level.Should().Be(AppLogLevel.Information);
        AppLogParser.Parse(fatal).Entries.Single().Level.Should().Be(AppLogLevel.Fatal);
    }

    [Fact]
    public void Parse_Ecs_ReadsSourceFromTheNodePilotNamespace_AndStackFromTheErrorObject()
    {
        var line = Ecs(Event(LogEventLevel.Warning, "Retry {Attempt}", Stamp,
            Thrown("transient"), "NodePilot.Remote.Winrm", ("Attempt", 2), ("WorkflowId", "wf-1")));

        var entry = AppLogParser.Parse(line).Entries.Should().ContainSingle().Subject;

        entry.Level.Should().Be(AppLogLevel.Warning);
        entry.Source.Should().Be("NodePilot.Remote.Winrm");
        entry.Message.Should().Be("Retry 2");
        entry.Template.Should().BeNull("ECS carries only the rendered message");
        entry.Details.Should().Contain("transient").And.Contain("workflow_id=wf-1").And.NotContain("source_context");
        entry.Format.Should().Be(AppLogParser.FormatEcs);
    }

    [Fact]
    public void Parse_Ecs_FatalSurvives()
    {
        var line = Ecs(Event(LogEventLevel.Fatal, "dead", Stamp));

        AppLogParser.Parse(line).Entries.Single().Level.Should().Be(AppLogLevel.Fatal);
    }

    [Fact]
    public void Parse_MixedFormatsInOneFile_AreParsedPerLine()
    {
        var lines = CmTrace(Event(LogEventLevel.Information, "one", Stamp))
            .Concat(Clef(Event(LogEventLevel.Warning, "two", Stamp.AddSeconds(1))))
            .Concat(Ecs(Event(LogEventLevel.Error, "three", Stamp.AddSeconds(2))))
            .Concat(Text(Event(LogEventLevel.Information, "four", Stamp.AddSeconds(3))));

        var result = AppLogParser.Parse(lines, TimeZoneInfo.Utc);

        result.Entries.Select(e => e.Message).Should().Equal("one", "two", "three", "four");
        result.Entries.Select(e => e.Format).Should().Equal(
            AppLogParser.FormatCmTrace, AppLogParser.FormatJson, AppLogParser.FormatEcs, AppLogParser.FormatText);
    }

    [Fact]
    public void Parse_BrokenStructuredLines_AreCountedNotShownAsInfo()
    {
        var good = CmTrace(Event(LogEventLevel.Information, "fine", Stamp));
        var lines = good.Concat(["<![LOG[cut off in the middle", "{\"@t\":\"2026-10-12T10:", "stray words"]);

        var result = AppLogParser.Parse(lines);

        result.Entries.Should().ContainSingle().Which.Message.Should().Be("fine");
        result.UnparsedLines.Should().Be(3);
        result.FragmentsDropped.Should().Be(0);
    }

    [Fact]
    public void Parse_LinesBeforeTheFirstEntry_AreDroppedAsFragments()
    {
        var lines = new[] { "   at NodePilot.Engine.Foo.Bar()", "   at System.Threading.Tasks.Task.Run()" }
            .Concat(Text(Event(LogEventLevel.Information, "first real", Stamp)));

        var result = AppLogParser.Parse(lines, TimeZoneInfo.Utc);

        result.FragmentsDropped.Should().Be(2);
        result.Entries.Should().ContainSingle().Which.Message.Should().Be("first real");
    }
}
