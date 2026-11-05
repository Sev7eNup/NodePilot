using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Scheduler;
using NodePilot.Scheduler.Sources;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// Runs <see cref="EventLogTriggerSource"/> against the real Application log through a dedicated
/// event source. Registering that source needs administrator rights, which the Windows CI runner
/// has; a local run without elevation skips unless the source is already registered, CI fails.
/// The source stays registered so later runs do not depend on the OS picking up a fresh one.
/// </summary>
public sealed class EventLogTriggerSourceLiveTests
{
    private const string TestSource = "NodePilot.Tests.EventLogTrigger";
    private const int MatchingEventId = 4242;
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task LiveEntries_DeliverOnlyTheEntryThatPassesEveryFilter()
    {
        EnsureTestSource();
        var token = Guid.NewGuid().ToString("N");
        var recorder = new Recorder(stored: null);
        await using var source = NewSource();

        await source.StartAsync(recorder.Context(token), TestContext.Current.CancellationToken);
        recorder.Initialized.Should().ContainSingle().Which.Version.Should().StartWith("eventlog-seed:");

        EventLog.WriteEntry(TestSource, $"{token} wrong id", EventLogEntryType.Warning, MatchingEventId + 1);
        EventLog.WriteEntry(TestSource, $"{token} wrong type", EventLogEntryType.Information, MatchingEventId);
        EventLog.WriteEntry(TestSource, "no token", EventLogEntryType.Warning, MatchingEventId);
        EventLog.WriteEntry(TestSource, $"{token} matching", EventLogEntryType.Warning, MatchingEventId);
        var signal = await recorder.FirstDelivery.WaitAsync(DeliveryTimeout, TestContext.Current.CancellationToken);

        // The three non-matching entries precede the match, so they were evaluated and skipped first.
        signal.Parameters["eventMessage"].Should().Contain($"{token} matching");
        signal.Parameters["eventSource"].Should().Be(TestSource);
        signal.Parameters["eventId"].Should().Be(MatchingEventId.ToString());
        signal.Parameters["eventEntryType"].Should().Be("Warning");
        signal.EventKey.Should().StartWith("eventlog:Application:");
        var cursor = Cursor(signal.Position);
        signal.EventKey.Should().EndWith($":{cursor.Generation}:{cursor.Index}");
        source.Health.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task Restart_CursorBehindLog_SkipsEntriesWrittenWhileDownAndKeepsGeneration()
    {
        EnsureTestSource();
        var token = Guid.NewGuid().ToString("N");
        EventLog.WriteEntry(TestSource, $"{token} while down", EventLogEntryType.Warning, MatchingEventId);
        var top = TopIndex();
        var recorder = new Recorder(new EventLogTriggerSource.EventLogCursor("gen-before-restart", top - 1));
        await using var source = NewSource();

        await source.StartAsync(recorder.Context(token), TestContext.Current.CancellationToken);
        // Startup persists its skip before any reconcile pass can save a checkpoint of its own.
        var skip = recorder.Saved.First();
        skip.Version.Should().StartWith("eventlog-skip:");
        Cursor(skip.Position).Should().Match<EventLogTriggerSource.EventLogCursor>(c =>
            c.Generation == "gen-before-restart" && c.Index >= top);

        EventLog.WriteEntry(TestSource, $"{token} after restart", EventLogEntryType.Warning, MatchingEventId);
        var signal = await recorder.FirstDelivery.WaitAsync(DeliveryTimeout, TestContext.Current.CancellationToken);

        signal.Parameters["eventMessage"].Should().Contain($"{token} after restart");
        signal.EventKey.Should().Contain(":gen-before-restart:");
    }

    [Fact]
    public async Task Restart_CursorAboveLog_TreatsTheLogAsClearedAndStartsANewGeneration()
    {
        EnsureTestSource();
        var token = Guid.NewGuid().ToString("N");
        var recorder = new Recorder(new EventLogTriggerSource.EventLogCursor("gen-old", int.MaxValue - 1));
        await using var source = NewSource();

        await source.StartAsync(recorder.Context(token), TestContext.Current.CancellationToken);
        var reset = recorder.Saved.First();
        reset.Version.Should().StartWith("eventlog-reset:");
        var generation = Cursor(reset.Position).Generation;
        generation.Should().NotBe("gen-old");

        EventLog.WriteEntry(TestSource, $"{token} after clear", EventLogEntryType.Warning, MatchingEventId);
        var signal = await recorder.FirstDelivery.WaitAsync(DeliveryTimeout, TestContext.Current.CancellationToken);

        signal.Parameters["eventMessage"].Should().Contain($"{token} after clear");
        signal.EventKey.Should().Contain($":{generation}:");
    }

    private static EventLogTriggerSource NewSource() => new(
        NullLogger<EventLogTriggerSource>.Instance,
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Trigger:EventLog:ReconcileSeconds"] = "1" })
            .Build());

    private static EventLogTriggerSource.EventLogCursor Cursor(string position)
        => JsonSerializer.Deserialize<EventLogTriggerSource.EventLogCursor>(position)
           ?? throw new InvalidOperationException($"Not a cursor: {position}");

    private static int TopIndex()
    {
        using var log = new EventLog("Application");
        var entries = log.Entries;
        return entries[entries.Count - 1].Index;
    }

    private static void EnsureTestSource()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                if (!EventLog.SourceExists(TestSource)) EventLog.CreateEventSource(TestSource, "Application");
                return;
            }
        }

        try
        {
            if (EventLog.SourceExists(TestSource)) return;
        }
        catch (SecurityException)
        {
            // Without elevation an unknown source cannot be told apart from an unreadable log.
        }
        if (Environment.GetEnvironmentVariable("CI") == "true")
            throw new InvalidOperationException($"CI must run elevated so the event source '{TestSource}' can be registered.");
        Assert.Skip($"Registering the event source '{TestSource}' needs administrator rights.");
    }

    private sealed class Recorder(EventLogTriggerSource.EventLogCursor? stored)
    {
        private readonly TaskCompletionSource<TriggerSignal> _firstDelivery =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<TriggerCheckpoint> _initialized = [];
        private readonly List<TriggerCheckpoint> _saved = [];

        public Task<TriggerSignal> FirstDelivery => _firstDelivery.Task;

        public IReadOnlyList<TriggerCheckpoint> Initialized { get { lock (_initialized) return [.. _initialized]; } }

        public IReadOnlyList<TriggerCheckpoint> Saved { get { lock (_saved) return [.. _saved]; } }

        public TriggerContext Context(string token) => new()
        {
            WorkflowId = Guid.NewGuid(),
            NodeId = "trg",
            Config = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                logName = "Application",
                source = TestSource,
                eventId = MatchingEventId,
                entryType = "Warning",
                messagePattern = token,
            })).RootElement.Clone(),
            OnFire = _ => Task.CompletedTask,
            OnDurableFire = signal =>
            {
                _firstDelivery.TrySetResult(signal);
                return Task.FromResult(true);
            },
            ReadCheckpoint = () => Task.FromResult<TriggerCheckpoint?>(stored is null
                ? null
                : new TriggerCheckpoint(JsonSerializer.Serialize(stored), "stored")),
            InitializeCheckpoint = checkpoint =>
            {
                lock (_initialized) _initialized.Add(checkpoint);
                return Task.FromResult(true);
            },
            SaveCheckpoint = checkpoint =>
            {
                lock (_saved) _saved.Add(checkpoint);
                return Task.FromResult(true);
            },
        };
    }
}
