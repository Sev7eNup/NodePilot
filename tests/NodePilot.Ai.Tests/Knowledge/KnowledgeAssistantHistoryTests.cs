using FluentAssertions;
using Moq;
using NodePilot.Ai.Knowledge;
using NodePilot.Core.Interfaces;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Ai.Tests.Knowledge;

public sealed class KnowledgeAssistantHistoryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task History_IsSentOnlyWithCurrentProfileContext(bool matchingContext)
    {
        var fake = new FakeLlmClient().EnqueueStream("answer");
        var factory = new FakeLlmClientFactory(fake);
        var options = LlmTestOptions.WithProfile();
        options.TryResolveActiveProfile(out var profile).Should().BeTrue();
        var key = LlmHistoryContext.Key(options.ActiveProfileId, profile!);
        var service = new KnowledgeAssistantService(factory, new PromptCatalog(), Mock.Of<IKnowledgeToolRegistry>(),
            new StaticOptionsMonitor<LlmOptions>(options), new StaticOptionsMonitor<AiKnowledgeOptions>(new()),
            Mock.Of<IOperationalKnowledgeReader>(), Mock.Of<ISettingsKnowledgeReader>(), Mock.Of<ISqlKnowledgeReader>());
        var request = new KnowledgeAskRequest("current question", [new("user", "previous operational context")],
            HistoryContextKey: matchingContext ? key : "old-provider-key");
        var events = new List<ChatStreamEvent>();
        await foreach (var evt in service.StreamAskAsync(request, AccessibleFolderSet.None, false, false, CancellationToken.None))
            events.Add(evt);

        fake.Calls.Should().ContainSingle();
        fake.Calls[0].Conversation!.Any(m => m.Content == "previous operational context").Should().Be(matchingContext);
        factory.Profiles.Should().ContainSingle().Which.Should().BeSameAs(profile);
        var done = events.OfType<ChatStreamEvent.DoneEvent>().Single();
        done.HistoryContextKey.Should().Be(key);
        done.HistoryDropped.Should().Be(!matchingContext);
    }
}
