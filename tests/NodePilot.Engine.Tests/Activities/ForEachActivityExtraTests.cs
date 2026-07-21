using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// Additional ForEachActivity coverage focusing on the parsing branches and the
/// MaxItemCount safety cap — these were uncovered in the original test set.
/// Most paths can be exercised without a real WorkflowEngine because the activity
/// short-circuits before the loop body when validation fails.
/// </summary>
public sealed class ForEachActivityExtraTests : IDisposable
{
    private readonly NodePilotDbContext _db = TestDbContext.Create();

    public void Dispose() => _db.Dispose();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private ForEachActivity CreateActivity() =>
        new(Mock.Of<IServiceScopeFactory>(), _db, new InMemorySubWorkflowGate());

    [Fact]
    public async Task ExecuteAsync_ItemsExceedMaxCount_ReturnsErrorAboutFiltering()
    {
        // Synthesise an array that exceeds the 10k cap. Use lines format so the input
        // payload stays compact (no JSON quoting for every element).
        var lines = string.Join('\n', Enumerable.Range(0, 10_001).Select(i => $"item{i}"));
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };
        var configJson = JsonSerializer.Serialize(new
        {
            childWorkflowNameOrId = "Child",
            items = lines,
            itemsFormat = "lines",
        });

        var result = await activity.ExecuteAsync(ctx, Parse(configJson), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("exceeds limit");
        result.ErrorOutput.Should().Contain("10000");
    }

    [Fact]
    public async Task ExecuteAsync_ChildNameDifferentCase_ResolvesCaseInsensitively()
    {
        // A disabled child proves the CI lookup FOUND the workflow: resolution happens
        // before the enabled check, so "disabled" (not "not found") is the expected error.
        _db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), Name = "Child-Job", DefinitionJson = "{}", IsEnabled = false,
        });
        await _db.SaveChangesAsync();
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx,
            Parse("""{"childWorkflowNameOrId":"child-job","items":"a","itemsFormat":"lines"}"""),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("disabled");
        result.ErrorOutput.Should().NotContain("not found");
    }

    [Fact]
    public async Task ExecuteAsync_AmbiguousChildName_FailsWithDisambiguationHint()
    {
        _db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Daily", DefinitionJson = "{}", IsEnabled = true });
        _db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "DAILY", DefinitionJson = "{}", IsEnabled = true });
        await _db.SaveChangesAsync();
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx,
            Parse("""{"childWorkflowNameOrId":"daily","items":"a","itemsFormat":"lines"}"""),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("multiple workflows").And.Contain("GUID");
    }

    [Fact]
    public async Task ExecuteAsync_AutoFormat_DetectsJsonArray_WithMixedTypes()
    {
        // Auto-mode treats "[…]" as JSON. Numbers and objects must round-trip via GetRawText
        // so the iteration index doesn't lose them. Validate by getting to the child-lookup
        // stage (Unknown -> "not found").
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx,
            Parse("""{"childWorkflowNameOrId":"Unknown","items":"[1,\"two\",{\"k\":\"v\"},null]"}"""),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("not found",
            "parser succeeded; failure must come from the subsequent child-lookup");
    }

    [Fact]
    public async Task ExecuteAsync_AutoFormat_NonArrayJsonObject_FallsBackToLines()
    {
        // "{...}" starts with `{` → auto-mode tries JSON first; doc.RootElement is an Object,
        // not Array. The branch falls through to line-split. The single line "{...}" then
        // becomes one item, which still fails at child-lookup with "not found".
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx,
            Parse("""{"childWorkflowNameOrId":"Unknown","items":"{\"oops\":1}"}"""),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("not found");
    }

    [Fact]
    public async Task ExecuteAsync_LinesFormat_TrimsWhitespaceAndDropsBlank()
    {
        // Cover the line-split branch with whitespace-padded entries + blank lines.
        // Reaching child-lookup means the parser correctly produced a non-empty list.
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx,
            Parse("""{"childWorkflowNameOrId":"Unknown","items":"  alpha  \n\nbeta\r\n  gamma","itemsFormat":"lines"}"""),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("not found");
    }

    [Fact]
    public async Task ExecuteAsync_JsonStrictNonArray_FailsWithExpectedMessage()
    {
        // Strict json mode + non-array input throws "expected JSON array, got Object" inside
        // ParseItems, surfaced as "failed to parse" by the activity's catch block.
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx,
            Parse("""{"childWorkflowNameOrId":"X","items":"{\"a\":1}","itemsFormat":"json"}"""),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("failed to parse");
        result.ErrorOutput.Should().Contain("expected JSON array");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyRawString_TreatedAsEmptyCollection()
    {
        // Empty string → ParseItems returns empty list → success/zero — the orig tests
        // covered the JSON "[]" route but not the "raw is empty after trim" branch.
        var activity = CreateActivity();
        var ctx = new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "fe1" };

        var result = await activity.ExecuteAsync(ctx,
            Parse("""{"childWorkflowNameOrId":"X","items":"   "}"""),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["total"].Should().Be("0");
    }
}
