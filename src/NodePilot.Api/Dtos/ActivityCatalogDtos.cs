using NodePilot.Core.Activities;

namespace NodePilot.Api.Dtos;

public sealed record ActivityCatalogEntryResponse(
    string Type,
    string Category,
    string LabelKey,
    string Icon,
    bool IsTrigger,
    bool IsExternalTrigger,
    bool IsRemote,
    string Timeout,
    IReadOnlyList<ActivityOutputParameterResponse> OutputParameters,
    IReadOnlyList<string> TelemetryParameters,
    ActivityPromptResponse Prompt)
{
    public static ActivityCatalogEntryResponse From(ActivityDescriptor descriptor) =>
        new(
            descriptor.Type,
            ToResponseCategory(descriptor.Category),
            descriptor.LabelKey,
            descriptor.Icon,
            descriptor.IsTrigger,
            descriptor.IsExternalTrigger,
            descriptor.IsRemote,
            ToResponseTimeout(descriptor.Timeout),
            descriptor.OutputParameters.Select(ActivityOutputParameterResponse.From).ToArray(),
            descriptor.TelemetryParameters.ToArray(),
            ActivityPromptResponse.From(descriptor.Prompt));

    private static string ToResponseCategory(ActivityCategory category) => category switch
    {
        ActivityCategory.Trigger => "trigger",
        ActivityCategory.Action => "action",
        ActivityCategory.ControlFlow => "controlFlow",
        ActivityCategory.Logic => "logic",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    private static string ToResponseTimeout(ActivityTimeoutKind timeout) => timeout switch
    {
        ActivityTimeoutKind.None => "none",
        ActivityTimeoutKind.Always => "always",
        ActivityTimeoutKind.WhenWaitForExit => "whenWaitForExit",
        ActivityTimeoutKind.WhenWaitForCompletion => "whenWaitForCompletion",
        _ => throw new ArgumentOutOfRangeException(nameof(timeout), timeout, null),
    };
}

public sealed record ActivityOutputParameterResponse(string Name, string Type)
{
    public static ActivityOutputParameterResponse From(ActivityOutputParameterDescriptor descriptor) =>
        new(descriptor.Name, descriptor.Type);
}

public sealed record ActivityPromptResponse(bool Included, string? ExclusionReason)
{
    public static ActivityPromptResponse From(ActivityPromptDescriptor descriptor) =>
        new(descriptor.IsIncluded, descriptor.ExclusionReason);
}
