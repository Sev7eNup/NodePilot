using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Triggers;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// IActivityExecutor coverage for <see cref="FileWatcherTrigger"/>. Two distinct shapes:
///
///   * Triggered execution: the FileWatcher source pushed manual.filePath/fileAction
///     into context.Variables → the activity surfaces those and returns immediately.
///   * Manual execution: someone hits "Run now" on the workflow → no manual.* present →
///     the activity scans the directory once.
/// </summary>
public class FileWatcherTriggerActivityTests
{
    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Execute_FailsFast_WhenDirectoryMissing()
    {
        var trigger = new FileWatcherTrigger();

        var result = await trigger.ExecuteAsync(new StepExecutionContext(), Cfg("""{}"""), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("directory");
    }

    [Fact]
    public async Task Execute_TriggeredFire_SurfacesPathAndAction()
    {
        // When the FSW source already fired this trigger, it pushed the file metadata as
        // manual.* into context.Variables. The activity must not re-scan the directory —
        // it should pass the metadata straight through to OutputParameters.
        var trigger = new FileWatcherTrigger();
        var ctx = new StepExecutionContext
        {
            Variables =
            {
                ["manual.filePath"] = @"C:\watched\new.log",
                ["manual.fileAction"] = "created",
                ["manual.fileName"] = "new.log",
            },
        };

        var result = await trigger.ExecuteAsync(ctx,
            Cfg("""{"directory":"C:\\watched","filter":"*.log"}"""),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["filePath"].Should().Be(@"C:\watched\new.log");
        result.OutputParameters["fileAction"].Should().Be("created");
        result.OutputParameters["fileName"].Should().Be("new.log");
        result.Output.Should().Contain("created");
        result.Output.Should().Contain("new.log");
    }

    [Fact]
    public async Task Execute_ManualScan_ReportsFileCount()
    {
        // No manual.* payload → "Run now" mode. Activity scans the directory once and
        // returns the list. This is what the editor's manual-fire button hits.
        var tempDir = Path.Combine(Path.GetTempPath(), "nodepilot-fwtrigger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "a.log"), "x");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "b.log"), "y");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "ignored.txt"), "z");

            var trigger = new FileWatcherTrigger();
            var configJson = $$"""{"directory":"{{tempDir.Replace("\\", "\\\\")}}","filter":"*.log"}""";

            var result = await trigger.ExecuteAsync(new StepExecutionContext(), Cfg(configJson), CancellationToken.None);

            result.Success.Should().BeTrue();
            result.Output.Should().Contain("Files found: 2");
            result.Output.Should().Contain("a.log");
            result.Output.Should().Contain("b.log");
            result.Output.Should().NotContain("ignored.txt");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Execute_ManualScan_DirectoryDoesNotExist_Fails()
    {
        var trigger = new FileWatcherTrigger();
        var nonExistent = Path.Combine(Path.GetTempPath(), "nopilot-missing-" + Guid.NewGuid().ToString("N"));
        var configJson = $$"""{"directory":"{{nonExistent.Replace("\\", "\\\\")}}"}""";

        var result = await trigger.ExecuteAsync(new StepExecutionContext(), Cfg(configJson), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("not found");
    }

    [Fact]
    public void ActivityType_IsFileWatcherTrigger() =>
        new FileWatcherTrigger().ActivityType.Should().Be("fileWatcherTrigger");
}
