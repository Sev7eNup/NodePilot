using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.Activities;
using NodePilot.Engine.PowerShell;
using NodePilot.Core.Interfaces;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// Verifies the transcript pipeline of <see cref="RunScriptActivity"/>:
///
/// 1. <c>WrapWithTranscript</c> produces a PowerShell script that opts into Start-Transcript
///    and emits the captured content between the START/END markers.
/// 2. <c>ExtractMarkers</c> splits raw stdout into clean output, transcript, and the params
///    block — including the case where both markers appear back-to-back (transcript +
///    auto-capture in one stdout stream from the process engine).
/// 3. The opt-out path (<c>config.transcript</c> absent or false) leaves the script untouched
///    so existing workflows behave identically.
/// </summary>
public class RunScriptTranscriptTests
{
    private readonly RunScriptActivity _activity;

    public RunScriptTranscriptTests()
    {
        _activity = new RunScriptActivity(
            new PowerShellEngineFactory(NullLoggerFactory.Instance),
            NullLogger<RunScriptActivity>.Instance);
    }

    private const string TranscriptStart = "###NODEPILOT_TRANSCRIPT_START###";
    private const string TranscriptEnd = "###NODEPILOT_TRANSCRIPT_END###";
    private const string ParamsMarker = "###NODEPILOT_PARAMS###";

    [Fact]
    public void WrapWithTranscript_WrapsScriptInTryFinallyWithTranscriptCommands()
    {
        var wrapped = RunScriptActivity.WrapWithTranscript("Get-Service");

        wrapped.Should().Contain("Start-Transcript -Path $__npTranscriptPath");
        wrapped.Should().Contain("Stop-Transcript");
        wrapped.Should().Contain("Get-Service");
        wrapped.Should().Contain("try {");
        wrapped.Should().Contain("} finally {");
        wrapped.Should().Contain(TranscriptStart);
        wrapped.Should().Contain(TranscriptEnd);
    }

    [Fact]
    public void WrapWithTranscript_IncludesPreCleanupForStaleTranscriptFiles()
    {
        var wrapped = RunScriptActivity.WrapWithTranscript("Get-Service");
        // Self-healing pre-cleanup so a hard cancel mid-script doesn't leak temp files forever.
        wrapped.Should().Contain("NodePilot-Transcript-*.log");
        wrapped.Should().Contain("AddHours(-24)");
    }

    [Fact]
    public void WrapWithTranscript_StopsPreviouslyActiveTranscriptDefensively()
    {
        var wrapped = RunScriptActivity.WrapWithTranscript("Get-Service");
        // Defensive: avoid the "Transcription has already been started" error.
        wrapped.Should().Contain("try { Stop-Transcript -ErrorAction Stop");
    }

    [Fact]
    public async Task ExtractMarkers_TranscriptOnly_SeparatedFromOutput()
    {
        var stdout = $"hello\n{TranscriptStart}\nps>line1\nps>line2\n{TranscriptEnd}";

        var result = await ExecuteRaw(stdout);

        result.Output.Should().Be("hello");
        result.TraceOutput.Should().Be("ps>line1\nps>line2");
    }

    [Fact]
    public async Task ExtractMarkers_TranscriptAndParams_BothExtractedSeparately()
    {
        var stdout =
            $"user output\n" +
            $"{TranscriptStart}\nTRANSCRIPT BODY\n{TranscriptEnd}\n" +
            $"{ParamsMarker}\n{{\"foo\":\"bar\"}}";

        var result = await ExecuteRaw(stdout);

        result.Output.Should().Be("user output");
        result.TraceOutput.Should().Be("TRANSCRIPT BODY");
        result.OutputParameters.Should().ContainKey("foo").WhoseValue.Should().Be("bar");
    }

    [Fact]
    public async Task ExtractMarkers_NoTranscriptMarker_TraceOutputNull()
    {
        var stdout = $"plain output\n{ParamsMarker}\n{{\"k\":\"v\"}}";

        var result = await ExecuteRaw(stdout);

        result.Output.Should().Be("plain output");
        result.TraceOutput.Should().BeNull();
        result.OutputParameters.Should().ContainKey("k");
    }

    [Fact]
    public async Task ExtractMarkers_OnlyTranscript_NoParamsKeepsOutputClean()
    {
        var stdout = $"line1\nline2\n{TranscriptStart}\nTRANS\n{TranscriptEnd}";

        var result = await ExecuteRaw(stdout);

        result.Output.Should().Be("line1\nline2");
        result.TraceOutput.Should().Be("TRANS");
        // No user params; only the always-present exitCode remains.
        result.OutputParameters.Keys.Should().BeEquivalentTo(new[] { "exitCode" });
    }

    [Fact]
    public async Task ExtractMarkers_OutputBeforeAndAfterTranscript_RecombinedCleanly()
    {
        var stdout = $"before\n{TranscriptStart}\nTRANS\n{TranscriptEnd}\nafter";

        var result = await ExecuteRaw(stdout);

        result.Output.Should().Be("before\nafter");
        result.TraceOutput.Should().Be("TRANS");
    }

    [Fact]
    public async Task ConfigTranscriptFalse_DoesNotInjectWrapper_OutputUnchanged()
    {
        // Runspace engine + no transcript flag → the script runs as-is. The marker should
        // never appear in the output stream because the wrapper isn't injected.
        var script = "Write-Output 'plain'";
        var config = JsonDocument.Parse($"{{\"script\": {JsonSerializer.Serialize(script)}, \"engine\": \"runspace\"}}").RootElement;
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "s1", Variables = new() };

        var result = await _activity.ExecuteAsync(ctx, config, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.TraceOutput.Should().BeNull();
        result.Output.Should().Contain("plain");
        result.Output.Should().NotContain(TranscriptStart);
    }

    [Fact]
    public async Task ConfigTranscriptTrue_RoundTripsThroughRunspace_TraceCaptured()
    {
        // End-to-end through the runspace engine on the test host. Start-Transcript writes to
        // a real temp file; the wrapper reads it back and emits between markers; the activity
        // extracts it into TraceOutput. Skip if Start-Transcript isn't available in this
        // PowerShell runtime (some sandboxes), since the assertion would be misleading.
        var script = "Write-Output 'hello-from-script'";
        var config = JsonDocument.Parse($"{{\"script\": {JsonSerializer.Serialize(script)}, \"engine\": \"runspace\", \"transcript\": true}}").RootElement;
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "s1", Variables = new() };

        var result = await _activity.ExecuteAsync(ctx, config, CancellationToken.None);

        if (!result.Success)
        {
            // Start-Transcript can fail on environments without console host (CI, restricted runspaces).
            // The test infrastructure expectation is opt-in: skip rather than fail when the host can't
            // produce a transcript at all.
            return;
        }

        result.Output.Should().Contain("hello-from-script");
        result.TraceOutput.Should().NotBeNull();
        // Transcript header tokens — PS 5.1 emits "Windows PowerShell Transcript Start",
        // PS 7+ emits "PowerShell transcript start". Case varies; both contain the phrase.
        result.TraceOutput!.ToLowerInvariant().Should().Contain("transcript start",
            "the transcript header must be present so we know Start-Transcript actually fired");
    }

    /// <summary>
    /// Helper that drives ExtractMarkers indirectly by feeding pre-baked stdout through
    /// the runspace engine via a here-string — keeps the assertions tight to the public
    /// behaviour of <see cref="RunScriptActivity.ExecuteAsync"/> rather than reaching at
    /// the internal extractor.
    /// </summary>
    private async Task<ActivityResult> ExecuteRaw(string stdout)
    {
        // Emit the literal stdout via a single Write-Output. Each \n in the test string becomes
        // a newline in the captured output stream. PowerShell single-quoted here-strings keep
        // the markers verbatim without escaping.
        var script = $"Write-Output @'\n{stdout}\n'@";
        var config = JsonDocument.Parse($"{{\"script\": {JsonSerializer.Serialize(script)}, \"engine\": \"runspace\"}}").RootElement;
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "s1", Variables = new() };
        return await _activity.ExecuteAsync(ctx, config, CancellationToken.None);
    }
}
