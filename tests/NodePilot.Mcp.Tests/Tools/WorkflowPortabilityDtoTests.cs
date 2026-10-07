using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Api.Dtos;
using Xunit;

namespace NodePilot.Mcp.Tests.Tools;

public sealed class WorkflowPortabilityDtoTests
{
    [Fact]
    public void ExportImportRoundTrip_PreservesDependencyMetadataAndExplicitMapping()
    {
        const string json = """{"schema":"nodepilot-workflow-export/v1","exportVersion":1,"exportedAt":"2026-09-23T00:00:00Z","workflow":{"name":"Team","definition":{},"sourceId":"00000000-0000-0000-0000-000000000001","dependencies":[{"kind":"skill","sourceId":"00000000-0000-0000-0000-000000000002","name":"diagnose","version":"1","sha256":"abc","targetId":"00000000-0000-0000-0000-000000000003"}]}}""";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var envelope = JsonSerializer.Deserialize<WorkflowExportEnvelope>(json, options)!;
        var roundTrip = JsonSerializer.Deserialize<WorkflowExportEnvelope>(JsonSerializer.Serialize(envelope, options), options)!;
        roundTrip.Workflow!.SourceId.Should().Be(envelope.Workflow!.SourceId).And.NotBeNull();
        roundTrip.Workflow.Dependencies.Should().ContainSingle().Which.Should().Be(envelope.Workflow.Dependencies![0]);
        roundTrip.Workflow.Dependencies![0].TargetId.Should().NotBeNull();
    }
}
