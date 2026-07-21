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
    public async Task StartAsync_Throws_WhenLogNameMissing()
    {
        var src = new EventLogTriggerSource(NullLogger<EventLogTriggerSource>.Instance, EmptyConfig());
        var act = () => src.StartAsync(Ctx("""{}"""), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*'logName' is required*");
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
}
