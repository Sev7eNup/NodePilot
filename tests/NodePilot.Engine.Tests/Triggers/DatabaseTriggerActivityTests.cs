using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Triggers;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// Coverage for the manual-run / orchestrator-fired branches of <see cref="DatabaseTrigger"/>.
/// Real-database execution is not tested here (would require SQL Server or polluting the
/// test DB) — those paths are owned by the SqlActivity / DatabaseTriggerSource tests.
/// </summary>
public class DatabaseTriggerActivityTests
{
    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    private static IConfiguration ConfigWith(params (string key, string val)[] entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            entries.ToDictionary(e => e.key, e => (string?)e.val)).Build();

    [Fact]
    public async Task Execute_OrchestratorFired_PassesThroughOutputParameters()
    {
        var trigger = new DatabaseTrigger(EmptyConfig());
        var ctx = new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "trg",
            Variables = new Dictionary<string, string>
            {
                // Orchestrator hands the trigger metadata in via manual.* keys
                ["manual.dbSentinel"] = "42",
                ["manual.dbPrevious"] = "41",
            },
        };

        var result = await trigger.ExecuteAsync(ctx, Cfg("""{}"""), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("41");
        result.Output.Should().Contain("42");
        result.OutputParameters.Should().ContainKey("dbSentinel").WhoseValue.Should().Be("42");
        result.OutputParameters.Should().ContainKey("dbPrevious").WhoseValue.Should().Be("41");
    }

    [Fact]
    public async Task Execute_ManualRun_NoQuery_ReturnsValidationFailure()
    {
        var trigger = new DatabaseTrigger(EmptyConfig());
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "trg" };

        var result = await trigger.ExecuteAsync(ctx, Cfg("""{}"""), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("No SQL query specified");
    }

    [Fact]
    public async Task Execute_ManualRun_MissingConnectionRef_ReturnsValidationFailure()
    {
        // Consistency fix: the manual executor now mirrors the scheduler-side source, which
        // expects a named `connectionRef` resolved against `Trigger:Database:Connections:{name}`.
        var trigger = new DatabaseTrigger(EmptyConfig());
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "trg" };

        var result = await trigger.ExecuteAsync(
            ctx,
            Cfg("""{"query":"SELECT 1","connectionRef":"NotConfigured"}"""),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("connectionRef");
        result.ErrorOutput.Should().Contain("NotConfigured");
    }

    [Fact]
    public async Task Execute_ManualRun_QueryTemplate_ReturnsValidationFailureBeforeDbOpen()
    {
        var trigger = new DatabaseTrigger(EmptyConfig());
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "trg" };

        var result = await trigger.ExecuteAsync(
            ctx,
            Cfg("""{"query":"SELECT MAX(Id) FROM T WHERE Id = {{prev.output}}","connectionString":"NotConfigured"}"""),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("{{...}} templates");
        result.ErrorOutput.Should().NotContain("Connection string",
            "template misuse must be rejected before any database configuration or network path is touched");
    }

    [Fact]
    public async Task Execute_ManualRun_BadConnectionString_SurfacesProviderError()
    {
        // connectionRef resolves via Trigger:Database:Connections:{name}. The connection string
        // points at a non-existent SQL Server — the SqlClient open throws and the trigger should
        // return Failure with a "Database error:" prefix.
        var config = ConfigWith(("Trigger:Database:Connections:Probe",
            "Server=tcp:127.0.0.1,1;Database=Nope;Connect Timeout=1;Encrypt=False;TrustServerCertificate=True"));
        var trigger = new DatabaseTrigger(config);
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "trg" };

        var result = await trigger.ExecuteAsync(
            ctx,
            Cfg("""{"query":"SELECT 1","connectionRef":"Probe"}"""),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().StartWith("Database error:");
    }

    [Fact]
    public void ActivityType_IsDatabaseTrigger() =>
        new DatabaseTrigger(EmptyConfig()).ActivityType.Should().Be("databaseTrigger");
}
