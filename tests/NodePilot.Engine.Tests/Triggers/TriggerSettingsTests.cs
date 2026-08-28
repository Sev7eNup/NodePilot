using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Triggers;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// The shared trigger contract both runtimes parse through. These assertions are the vocabulary
/// itself: every key here is one the designer writes, the reference documents, the node executor's
/// sample run honours and the background source applies.
/// </summary>
public class EventLogTriggerSettingsTests
{
    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Parse_NoKeys_AppliesDocumentedDefaults()
    {
        var settings = EventLogTriggerSettings.Parse(Cfg("{}"));

        settings.LogName.Should().Be("Application");
        settings.LookbackMinutes.Should().Be(5);
        settings.Source.Should().BeNull();
        settings.EventId.Should().BeNull();
        settings.EntryType.Should().BeNull();
        settings.MessagePattern.Should().BeNull();
    }

    [Fact]
    public void Parse_LevelAlias_MapsToEntryType()
    {
        EventLogTriggerSettings.Parse(Cfg("""{"level":"error"}""")).EntryType
            .Should().Be(EventLogEntryTypeFilter.Error);
    }

    [Fact]
    public void Parse_EntryTypeAndLevelBothPresent_PrefersEntryType()
    {
        EventLogTriggerSettings.Parse(Cfg("""{"entryType":"Warning","level":"error"}""")).EntryType
            .Should().Be(EventLogEntryTypeFilter.Warning);
    }

    [Theory]
    [InlineData("""{"eventId":4624}""")]
    [InlineData("""{"eventId":"4624"}""")]
    public void Parse_EventId_AcceptsNumberAndString(string json)
    {
        // Imported and AI-authored definitions routinely carry numbers as strings; a hard cast
        // would fail registration for a value the operator can see is fine.
        EventLogTriggerSettings.Parse(Cfg(json)).EventId.Should().Be(4624);
    }

    [Fact]
    public void Parse_LookbackMinutesBelowOne_ClampsToOne()
    {
        EventLogTriggerSettings.Parse(Cfg("""{"lookbackMinutes":0}""")).LookbackMinutes.Should().Be(1);
    }

    [Fact]
    public void Parse_InvalidMessagePattern_Throws()
    {
        var act = () => EventLogTriggerSettings.Parse(Cfg("""{"messagePattern":"(unbalanced"}"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*invalid messagePattern regex*");
    }

    [Fact]
    public void Matches_NoFilters_MatchesEverything()
    {
        EventLogTriggerSettings.Parse(Cfg("{}"))
            .Matches("AnySource", 1, EventLogEntryTypeFilter.Information, "anything")
            .Should().Be(EventLogMatch.Match);
    }

    [Fact]
    public void Matches_EventIdFilter_RejectsOtherIds()
    {
        var settings = EventLogTriggerSettings.Parse(Cfg("""{"eventId":4624}"""));

        settings.Matches("Src", 4624, EventLogEntryTypeFilter.Information, "m").Should().Be(EventLogMatch.Match);
        settings.Matches("Src", 4625, EventLogEntryTypeFilter.Information, "m").Should().Be(EventLogMatch.NoMatch);
    }

    [Fact]
    public void Matches_SourceFilter_IsCaseInsensitive()
    {
        var settings = EventLogTriggerSettings.Parse(Cfg("""{"source":"MyApp"}"""));

        settings.Matches("myapp", 1, EventLogEntryTypeFilter.Error, "m").Should().Be(EventLogMatch.Match);
        settings.Matches("Other", 1, EventLogEntryTypeFilter.Error, "m").Should().Be(EventLogMatch.NoMatch);
    }

    [Fact]
    public void Matches_EntryTypeFilter_RejectsOtherTypes()
    {
        var settings = EventLogTriggerSettings.Parse(Cfg("""{"entryType":"Error"}"""));

        settings.Matches("s", 1, EventLogEntryTypeFilter.Error, "m").Should().Be(EventLogMatch.Match);
        settings.Matches("s", 1, EventLogEntryTypeFilter.Warning, "m").Should().Be(EventLogMatch.NoMatch);
    }

    [Fact]
    public void Matches_MessagePattern_AppliesRegex()
    {
        var settings = EventLogTriggerSettings.Parse(Cfg("""{"messagePattern":"disk.*full"}"""));

        settings.Matches("s", 1, EventLogEntryTypeFilter.Error, "the disk is full").Should().Be(EventLogMatch.Match);
        settings.Matches("s", 1, EventLogEntryTypeFilter.Error, "all good").Should().Be(EventLogMatch.NoMatch);
        settings.Matches("s", 1, EventLogEntryTypeFilter.Error, null).Should().Be(EventLogMatch.NoMatch);
    }

    [Fact]
    public void Matches_AllFiltersSet_RequiresEveryOneToPass()
    {
        var settings = EventLogTriggerSettings.Parse(
            Cfg("""{"source":"MyApp","eventId":7,"entryType":"Error","messagePattern":"boom"}"""));

        settings.Matches("MyApp", 7, EventLogEntryTypeFilter.Error, "boom").Should().Be(EventLogMatch.Match);
        settings.Matches("MyApp", 7, EventLogEntryTypeFilter.Error, "quiet").Should().Be(EventLogMatch.NoMatch);
        settings.Matches("MyApp", 8, EventLogEntryTypeFilter.Error, "boom").Should().Be(EventLogMatch.NoMatch);
    }

    [Theory]
    [InlineData("Application")]
    [InlineData("System")]
    [InlineData("application")]
    public void IsLogAllowed_DefaultLogs_AreAllowedWithoutConfiguration(string logName)
    {
        EventLogTriggerSettings.IsLogAllowed(logName, null).Should().BeTrue();
    }

    [Fact]
    public void IsLogAllowed_Security_IsRejectedByDefault()
    {
        EventLogTriggerSettings.IsLogAllowed("Security", null).Should().BeFalse();
    }

    [Fact]
    public void IsLogAllowed_ConfiguredList_ExtendsRatherThanReplacesTheDefaults()
    {
        // Custom logs extend the Application and System defaults in both trigger runtimes.
        string[] configured = ["NodePilot-Custom"];

        EventLogTriggerSettings.IsLogAllowed("NodePilot-Custom", configured).Should().BeTrue();
        EventLogTriggerSettings.IsLogAllowed("Application", configured).Should().BeTrue();
        EventLogTriggerSettings.IsLogAllowed("Security", configured).Should().BeFalse();
    }

    [Fact]
    public void DescribeRejectedLog_NamesTheLogAndTheEscapeHatch()
    {
        var message = EventLogTriggerSettings.DescribeRejectedLog("Security", null);

        message.Should().Contain("'Security'").And.Contain("Trigger:EventLog:AllowedLogs");
    }
}

/// <summary>Same contract, database side.</summary>
public class DatabaseTriggerSettingsTests
{
    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    private const string MinimalQuery = """{"query":"SELECT MAX(Id) FROM T"}""";

    [Fact]
    public void Parse_NoQuery_Throws()
    {
        var act = () => DatabaseTriggerSettings.Parse(Cfg("{}"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*'query' is required*");
    }

    [Fact]
    public void Parse_TemplatedQuery_Throws()
    {
        // H-1: a trigger query runs before any step exists, so a {{var}} can never resolve.
        var act = () => DatabaseTriggerSettings.Parse(
            Cfg("""{"query":"SELECT * FROM T WHERE Id = {{step.output}}"}"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*must not contain {{...}} templates*");
    }

    [Fact]
    public void Parse_NoInterval_KeepsTheEstablishedThirtySecondCadence()
    {
        // An absent interval preserves the established 30-second polling cadence.
        DatabaseTriggerSettings.Parse(Cfg(MinimalQuery)).PollingIntervalSeconds.Should().Be(30);
    }

    [Theory]
    [InlineData("""{"query":"SELECT 1","pollingIntervalSeconds":120}""", 120)]
    [InlineData("""{"query":"SELECT 1","pollingIntervalSeconds":"120"}""", 120)]
    [InlineData("""{"query":"SELECT 1","pollingIntervalSeconds":1}""", 5)]
    public void Parse_PollingInterval_IsReadAndClampedToTheFloor(string json, int expected)
    {
        DatabaseTriggerSettings.Parse(Cfg(json)).PollingIntervalSeconds.Should().Be(expected);
    }

    [Fact]
    public void Parse_LegacyIntervalSecondsKey_StillConfiguresTheCadence()
    {
        // The poll loop's original spelling. A hand-written or imported definition that uses it
        // must not lose its configured cadence just because the two runtimes now share a parser.
        DatabaseTriggerSettings.Parse(Cfg("""{"query":"SELECT 1","intervalSeconds":90}"""))
            .PollingIntervalSeconds.Should().Be(90);
    }

    [Fact]
    public void Parse_BothIntervalKeys_PrefersPollingIntervalSeconds()
    {
        DatabaseTriggerSettings.Parse(
            Cfg("""{"query":"SELECT 1","pollingIntervalSeconds":15,"intervalSeconds":90}"""))
            .PollingIntervalSeconds.Should().Be(15);
    }

    [Fact]
    public void Parse_NoProvider_DefaultsToSqlServer()
    {
        DatabaseTriggerSettings.Parse(Cfg(MinimalQuery)).Provider.Should().Be("sqlserver");
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("SQLite")]
    public void Parse_SupportedProvider_IsNormalisedToLowercase(string provider)
    {
        DatabaseTriggerSettings.Parse(Cfg($$"""{"query":"SELECT 1","provider":"{{provider}}"}"""))
            .Provider.Should().Be("sqlite");
    }

    [Fact]
    public void Parse_UnsupportedProvider_Throws()
    {
        var act = () => DatabaseTriggerSettings.Parse(Cfg("""{"query":"SELECT 1","provider":"oracle"}"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*provider 'oracle' is not supported*");
    }

    [Fact]
    public void ResolveConnectionString_NamedRef_WinsOverInline()
    {
        var settings = DatabaseTriggerSettings.Parse(
            Cfg("""{"query":"SELECT 1","connectionRef":"Prod","connectionString":"Server=inline"}"""));

        settings.ResolveConnectionString(_ => "Server=fromConfig", requireRef: true)
            .Should().Be("Server=fromConfig");
    }

    [Fact]
    public void ResolveConnectionString_UnknownRef_Throws()
    {
        var settings = DatabaseTriggerSettings.Parse(Cfg("""{"query":"SELECT 1","connectionRef":"Nope"}"""));

        var act = () => settings.ResolveConnectionString(_ => null, requireRef: true);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'Nope' is not defined*");
    }

    [Fact]
    public void ResolveConnectionString_InlineWhileRefRequired_Throws()
    {
        // H-13: workflow JSON must not be able to carry plaintext DB credentials into the process.
        var settings = DatabaseTriggerSettings.Parse(
            Cfg("""{"query":"SELECT 1","connectionString":"Server=x;Password=secret"}"""));

        var act = () => settings.ResolveConnectionString(_ => null, requireRef: true);

        act.Should().Throw<InvalidOperationException>().WithMessage("*inline connectionString is disabled*");
    }

    [Fact]
    public void ResolveConnectionString_InlineWhenAllowed_IsReturned()
    {
        var settings = DatabaseTriggerSettings.Parse(
            Cfg("""{"query":"SELECT 1","connectionString":"Data Source=:memory:"}"""));

        settings.ResolveConnectionString(_ => null, requireRef: false).Should().Be("Data Source=:memory:");
    }

    [Fact]
    public void ResolveConnectionString_NeitherRefNorInline_Throws()
    {
        var settings = DatabaseTriggerSettings.Parse(Cfg(MinimalQuery));

        var act = () => settings.ResolveConnectionString(_ => null, requireRef: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*either 'connectionRef'*");
    }
}
