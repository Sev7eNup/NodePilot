using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Scheduler;
using NodePilot.Scheduler.Sources;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// Validation-path coverage for <see cref="EventLogTriggerSource"/>. The OnEntry filter
/// logic (source/type/regex) is not exercised here because the runtime payload
/// (<c>EntryWrittenEventArgs</c> wrapping a sealed <c>EventLogEntry</c>) cannot be
/// constructed from test code without reflection-hacking the Win32 layer. The filter
/// itself is a half-dozen string comparisons + a regex match - low complexity, low risk.
/// What we DO test is everything that runs before the EventLog subscription is created:
/// missing config, log-allow-list enforcement, regex parse errors, and dispose safety.
/// </summary>
public class EventLogTriggerSourceTests
{
    private static JsonElement ParseConfig(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    private static IConfiguration WithAllowedLogs(params string[] names)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < names.Length; i++)
            dict[$"Trigger:EventLog:AllowedLogs:{i}"] = names[i];
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static TriggerContext Ctx(string configJson) => new()
    {
        WorkflowId = Guid.NewGuid(),
        NodeId = "trg",
        Config = ParseConfig(configJson),
        OnFire = _ => Task.CompletedTask,
    };

    [Fact]
    public void Parse_DefaultsToApplication_WhenLogNameMissing()
    {
        // The source and node executor share the documented Application default. Parse directly
        // so the test does not open a real Windows EventLog.
        var settings = NodePilot.Core.Triggers.EventLogTriggerSettings.Parse(ParseConfig("""{}"""));

        settings.LogName.Should().Be("Application");
    }

    [Fact]
    public async Task StartAsync_Throws_WhenLogNotInAllowList()
    {
        // 'Security' is intentionally NOT in the default allow-list (Application, System).
        var src = new EventLogTriggerSource(NullLogger<EventLogTriggerSource>.Instance, EmptyConfig());
        var act = () => src.StartAsync(Ctx("""{"logName":"Security"}"""), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*log 'Security' is not allowed*");
    }

    [Fact]
    public async Task StartAsync_Throws_WhenCustomLogIsNotInConfigAllowList()
    {
        var src = new EventLogTriggerSource(NullLogger<EventLogTriggerSource>.Instance, EmptyConfig());
        var act = () => src.StartAsync(Ctx("""{"logName":"NodePilot-Custom"}"""), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not allowed*");
    }

    [Fact]
    public async Task StartAsync_RejectsLogNotInConfigAllowList_EvenWithOtherEntriesPresent()
    {
        var src = new EventLogTriggerSource(
            NullLogger<EventLogTriggerSource>.Instance,
            WithAllowedLogs("CustomLogA", "CustomLogB"));

        var act = () => src.StartAsync(Ctx("""{"logName":"CustomLogC"}"""), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not allowed*");
    }

    [Fact]
    public async Task StartAsync_Throws_WhenMessagePatternIsInvalidRegex()
    {
        // Config triggers regex compilation BEFORE creating the EventLog object, so the
        // throw happens without touching Windows EventLog at all. We use 'Application' so the
        // allow-list passes; the regex `(` is unbalanced and must fail compilation.
        var src = new EventLogTriggerSource(NullLogger<EventLogTriggerSource>.Instance, EmptyConfig());
        var act = () => src.StartAsync(
            Ctx("""{"logName":"Application","messagePattern":"(unbalanced"}"""),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid messagePattern regex*");
    }

    [Fact]
    public async Task DisposeAsync_IsSafe_WhenStartAsyncWasNeverCalled()
    {
        var src = new EventLogTriggerSource(NullLogger<EventLogTriggerSource>.Instance, EmptyConfig());

        // Must not throw - the source initializes lazily inside StartAsync.
        await src.DisposeAsync();
    }

    [Fact]
    public void PlanSkip_AdvancesStaleCursorToTopOfLog_KeepingGeneration()
    {
        // Entries written while the source was down are never replayed: the cursor jumps straight
        // to the top of the log. The generation must survive the jump — a new one is only minted
        // when the log was cleared.
        var stale = new EventLogTriggerSource.EventLogCursor("gen-a", 100);

        var planned = EventLogTriggerSource.PlanSkip(stale, 420);

        planned.Should().NotBeNull();
        planned!.Index.Should().Be(420);
        planned.Generation.Should().Be("gen-a");
    }

    [Fact]
    public void PlanSkip_ReturnsNull_WhenCursorIsAlreadyCurrent()
    {
        var current = new EventLogTriggerSource.EventLogCursor("gen-a", 420);

        EventLogTriggerSource.PlanSkip(current, 420).Should().BeNull();
    }

    [Fact]
    public void PlanSkip_ReturnsNull_WhenCursorIsAheadOfLog()
    {
        // A cursor above the top index means the log was cleared. That is the reset path's job,
        // not the skip path's — the skip only ever moves forward.
        var ahead = new EventLogTriggerSource.EventLogCursor("gen-a", 500);

        EventLogTriggerSource.PlanSkip(ahead, 12).Should().BeNull();
    }

    [Fact]
    public void PlanSkip_ReturnsNull_WhenThereIsNoCursorYet()
    {
        // No cursor means a fresh seed, which already lands on the top of the log.
        EventLogTriggerSource.PlanSkip(null, 420).Should().BeNull();
    }
}
