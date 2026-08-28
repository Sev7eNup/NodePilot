using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// The descriptor hash is persisted into <c>TriggerDeliveryCheckpoint.ConfigurationHash</c>, a
/// fixed-width column. It therefore has to be a digest of bounded length, not the config itself.
/// </summary>
public class TriggerConfigHashTests
{
    private static WorkflowDefinitionDocument Parse(string definitionJson)
        => WorkflowDefinitionDocument.Parse(definitionJson);

    private static string Definition(string directory)
        => JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new
                {
                    id = "trg",
                    type = "activity",
                    position = new { x = 0, y = 0 },
                    data = new
                    {
                        label = "Watch",
                        activityType = "fileWatcherTrigger",
                        config = new { directory, filter = "*.txt", watchType = "created" },
                    },
                },
            },
            edges = Array.Empty<object>(),
        });

    [Fact]
    public void Hash_StaysWithinTheCheckpointColumn_ForALongConfig()
    {
        // A deep UNC path or a real SQL query easily exceeds the 128-character column; before the
        // hash was a digest, such a trigger failed to persist its checkpoint and never registered.
        var longPath = "//fileserver01.corp.example.com/departments/finance/incoming/"
            + string.Join('/', Enumerable.Repeat("nested-folder-with-a-long-name", 8));
        var doc = Parse(Definition(longPath));

        var descriptor = doc.TriggerDescriptors.Should().ContainSingle().Subject;
        longPath.Length.Should().BeGreaterThan(128, "the test input has to be the interesting case");
        descriptor.Hash.Length.Should().Be(64);
    }

    [Fact]
    public void Hash_DiffersWhenTheConfigChanges()
    {
        var a = Parse(Definition(@"C:\watch\a")).TriggerDescriptors[0].Hash;
        var b = Parse(Definition(@"C:\watch\b")).TriggerDescriptors[0].Hash;

        a.Should().NotBe(b, "a config change must re-register the trigger");
    }

    [Fact]
    public void Hash_IsStableForTheSameConfig()
    {
        var a = Parse(Definition(@"C:\watch\a")).TriggerDescriptors[0].Hash;
        var b = Parse(Definition(@"C:\watch\a")).TriggerDescriptors[0].Hash;

        a.Should().Be(b, "an unchanged trigger must not be torn down on every scan");
    }
}
