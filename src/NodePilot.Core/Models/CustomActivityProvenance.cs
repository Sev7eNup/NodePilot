namespace NodePilot.Core.Models;

/// <summary>
/// Identity of the custom-activity definition version that produced a step result. Set by
/// <c>CustomActivityExecutor</c> and stored on <c>StepExecution</c> so a past run stays traceable
/// after the live definition changes. <see cref="Hash"/> covers the script template and the
/// normalized execution options.
/// </summary>
public sealed record CustomActivityProvenance(string Key, int Version, string Hash);
