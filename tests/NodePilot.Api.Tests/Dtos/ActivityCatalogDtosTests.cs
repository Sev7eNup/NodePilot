using FluentAssertions;
using NodePilot.Api.Dtos;
using NodePilot.Core.Activities;
using Xunit;

namespace NodePilot.Api.Tests.DtoMapping;

/// <summary>
/// Unit coverage for the <see cref="ActivityCatalogEntryResponse"/> mapping surface that the
/// designer-palette endpoint (<c>GET /api/activity-catalog</c>) serves. The enum→wire-token
/// projections (category, timeout) are the contract the frontend catalog mirror depends on.
/// </summary>
public class ActivityCatalogDtosTests
{
    [Fact]
    public void From_ActionDescriptor_MapsEveryField()
    {
        var descriptor = new ActivityDescriptor("runScript", ActivityCategory.Action, "runScript.label", "terminal")
        {
            IsRemote = true,
            IsExternalTrigger = false,
            Timeout = ActivityTimeoutKind.Always,
            OutputParameters = new[]
            {
                new ActivityOutputParameterDescriptor("exitCode", "number"),
                new ActivityOutputParameterDescriptor("stdout", "string"),
            },
            TelemetryParameters = new[] { "exitCode" },
            Prompt = ActivityPromptDescriptor.Included,
        };

        var dto = ActivityCatalogEntryResponse.From(descriptor);

        dto.Type.Should().Be("runScript");
        dto.Category.Should().Be("action");
        dto.LabelKey.Should().Be("runScript.label");
        dto.Icon.Should().Be("terminal");
        dto.IsTrigger.Should().BeFalse();
        dto.IsExternalTrigger.Should().BeFalse();
        dto.IsRemote.Should().BeTrue();
        dto.Timeout.Should().Be("always");
        dto.OutputParameters.Should().HaveCount(2);
        dto.OutputParameters[0].Name.Should().Be("exitCode");
        dto.OutputParameters[0].Type.Should().Be("number");
        dto.OutputParameters[1].Name.Should().Be("stdout");
        dto.TelemetryParameters.Should().ContainSingle().Which.Should().Be("exitCode");
        dto.Prompt.Included.Should().BeTrue();
        dto.Prompt.ExclusionReason.Should().BeNull();
    }

    [Fact]
    public void From_TriggerDescriptor_MapsTriggerFlags()
    {
        var descriptor = new ActivityDescriptor("scheduleTrigger", ActivityCategory.Trigger, "scheduleTrigger.label", "schedule")
        {
            IsExternalTrigger = true,
            OutputParameters = new[] { new ActivityOutputParameterDescriptor("firedAt", "string") },
        };

        var dto = ActivityCatalogEntryResponse.From(descriptor);

        dto.Category.Should().Be("trigger");
        dto.IsTrigger.Should().BeTrue();
        dto.IsExternalTrigger.Should().BeTrue();
        dto.IsRemote.Should().BeFalse();
        dto.Timeout.Should().Be("none");
    }

    [Fact]
    public void From_ExcludedPrompt_CarriesExclusionReason()
    {
        var descriptor = new ActivityDescriptor("fileHash", ActivityCategory.Action, "fileHash.label", "tag")
        {
            Prompt = ActivityPromptDescriptor.Excluded("niche activity"),
        };

        var dto = ActivityCatalogEntryResponse.From(descriptor);

        dto.Prompt.Included.Should().BeFalse();
        dto.Prompt.ExclusionReason.Should().Be("niche activity");
    }

    [Theory]
    [InlineData(ActivityCategory.Trigger, "trigger")]
    [InlineData(ActivityCategory.Action, "action")]
    [InlineData(ActivityCategory.ControlFlow, "controlFlow")]
    [InlineData(ActivityCategory.Logic, "logic")]
    public void From_MapsEveryCategoryToken(ActivityCategory category, string expected)
    {
        var descriptor = new ActivityDescriptor("x", category, "x.label", "icon");
        ActivityCatalogEntryResponse.From(descriptor).Category.Should().Be(expected);
    }

    [Theory]
    [InlineData(ActivityTimeoutKind.None, "none")]
    [InlineData(ActivityTimeoutKind.Always, "always")]
    [InlineData(ActivityTimeoutKind.WhenWaitForExit, "whenWaitForExit")]
    [InlineData(ActivityTimeoutKind.WhenWaitForCompletion, "whenWaitForCompletion")]
    public void From_MapsEveryTimeoutToken(ActivityTimeoutKind kind, string expected)
    {
        var descriptor = new ActivityDescriptor("x", ActivityCategory.Action, "x.label", "icon") { Timeout = kind };
        ActivityCatalogEntryResponse.From(descriptor).Timeout.Should().Be(expected);
    }

    [Fact]
    public void From_UnknownCategory_Throws()
    {
        var descriptor = new ActivityDescriptor("x", (ActivityCategory)99, "x.label", "icon");
        var act = () => ActivityCatalogEntryResponse.From(descriptor);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void From_UnknownTimeout_Throws()
    {
        var descriptor = new ActivityDescriptor("x", ActivityCategory.Action, "x.label", "icon")
        {
            Timeout = (ActivityTimeoutKind)99,
        };
        var act = () => ActivityCatalogEntryResponse.From(descriptor);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void From_EveryRealCatalogEntry_MapsWithoutThrowing()
    {
        // Guard: the whole shipped catalog must project cleanly (no unmapped enum value).
        foreach (var descriptor in ActivityCatalog.All)
        {
            var dto = ActivityCatalogEntryResponse.From(descriptor);
            dto.Type.Should().Be(descriptor.Type);
            dto.OutputParameters.Should().HaveCount(descriptor.OutputParameters.Count);
            dto.TelemetryParameters.Should().HaveCount(descriptor.TelemetryParameters.Count);
        }
    }

    [Fact]
    public void ActivityOutputParameterResponse_From_MapsNameAndType()
    {
        var dto = ActivityOutputParameterResponse.From(new ActivityOutputParameterDescriptor("count", "number"));
        dto.Name.Should().Be("count");
        dto.Type.Should().Be("number");
    }

    [Fact]
    public void ActivityPromptResponse_From_MapsIncludedAndReason()
    {
        ActivityPromptResponse.From(ActivityPromptDescriptor.Included).Should()
            .BeEquivalentTo(new ActivityPromptResponse(true, null));
        ActivityPromptResponse.From(ActivityPromptDescriptor.Excluded("why")).Should()
            .BeEquivalentTo(new ActivityPromptResponse(false, "why"));
    }
}
