namespace NodePilot.Core.Models;

/// <summary>
/// Identity of the exact custom-activity definition version that produced a step result. Set by
/// <c>CustomActivityExecutor</c> and persisted onto <c>StepExecution</c> so a past run stays
/// reproducible even after latest-wins edits or a rollback change the live definition. The
/// <see cref="Hash"/> is over the script template + normalized execution options.
/// </summary>
public sealed record CustomActivityProvenance(string Key, int Version, string Hash);
