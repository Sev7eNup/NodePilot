using System.Text.Json;
using FluentAssertions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

public class PowerShellOperationTests
{
    [Fact]
    public void Markers_RendersNodePilotResultEnvelope()
    {
        var markers = PowerShellOperation.Markers("registry");

        markers.Start.Should().Be("###NODEPILOT_REGISTRY_RESULT_START###");
        markers.End.Should().Be("###NODEPILOT_REGISTRY_RESULT_END###");
        markers.RenderJsonEnvelope("$__result", depth: 4)
            .Should().Contain("Write-Output '###NODEPILOT_REGISTRY_RESULT_START###'")
            .And.Contain("Write-Output ($__result | ConvertTo-Json -Depth 4 -Compress)")
            .And.Contain("Write-Output '###NODEPILOT_REGISTRY_RESULT_END###'");
    }

    [Fact]
    public void TryExtractJsonBlock_ReturnsJsonAndLeadingOutput()
    {
        var markers = PowerShellOperation.Markers("program");
        var output = "prelude\n###NODEPILOT_PROGRAM_RESULT_START###\n{\"ok\":true}\n###NODEPILOT_PROGRAM_RESULT_END###\ntrailing";

        PowerShellOperation.TryExtractJsonBlock(output, markers, out var block).Should().BeTrue();

        block.Json.Should().Be("{\"ok\":true}");
        block.LeadingOutput.Should().Be("prelude");
    }

    [Fact]
    public void TryParseJsonBlock_InvalidJson_ReturnsParseError()
    {
        var markers = PowerShellOperation.Markers("zip");
        var output = "###NODEPILOT_ZIP_RESULT_START###\n{broken\n###NODEPILOT_ZIP_RESULT_END###";

        PowerShellOperation.TryParseJsonBlock(output, markers, out var doc, out var error).Should().BeFalse();

        doc.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CopyStringField_UsesActivityScalarSemantics()
    {
        using var doc = JsonDocument.Parse("""{"s":"text","n":42,"b":true,"nullValue":null,"obj":{"x":1}}""");
        var dest = new Dictionary<string, string>();

        PowerShellOperation.CopyStringField(doc.RootElement, "s", dest, "s");
        PowerShellOperation.CopyStringField(doc.RootElement, "n", dest, "n");
        PowerShellOperation.CopyStringField(doc.RootElement, "b", dest, "b");
        PowerShellOperation.CopyStringField(doc.RootElement, "nullValue", dest, "nullValue");
        PowerShellOperation.CopyStringField(doc.RootElement, "obj", dest, "obj");

        dest.Should().Contain("s", "text")
            .And.Contain("n", "42")
            .And.Contain("b", "true")
            .And.Contain("nullValue", "")
            .And.Contain("obj", "{\"x\":1}");
    }

    [Fact]
    public void TimeoutHelpers_MapConfigToEngineAndProcessTimeouts()
    {
        using var doc = JsonDocument.Parse("""{"timeoutSeconds":45}""");

        var seconds = PowerShellOperation.TimeoutSecondsFromConfig(doc.RootElement);

        seconds.Should().Be(45);
        PowerShellOperation.ToTimeSpan(seconds).Should().Be(TimeSpan.FromSeconds(45));
        PowerShellOperation.ToWaitForExitMilliseconds(seconds).Should().Be(45_000);
        PowerShellOperation.ToWaitForExitMilliseconds(null).Should().Be(-1);
    }

    [Fact]
    public void Extractors_HandleCommonOperationOutputShapes()
    {
        PowerShellOperation.ExtractLastIntegerLine("\nnot it\n123\n").Should().Be("123");
        PowerShellOperation.ExtractLastIntegerLine("no integers").Should().Be("0");
    }
}
