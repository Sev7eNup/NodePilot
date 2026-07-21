namespace NodePilot.Core.Models;

/// <summary>
/// A user-authored, reusable workflow activity backed by a parameterized PowerShell template.
/// Surfaces in the designer as a "Custom Node" (palette label) and executes via the shared
/// runScript machinery — engine selection, process isolation, marker-based structured output,
/// secret redaction, exit-code semantics, local/remote routing. A custom activity is, in effect,
/// a reusable runScript preset; it introduces no second script engine.
///
/// <para>
/// A workflow node references a definition through <c>data.config.__customDefinitionId</c> (the
/// authoritative link) and carries the routing type <c>custom:&lt;Key&gt;</c> as its
/// <c>activityType</c>. The engine resolves any <c>custom:*</c> type to the single
/// <c>CustomActivityExecutor</c>, which loads this row, verifies the key matches, injects the
/// resolved inputs as PowerShell variables and captures only the declared outputs.
/// </para>
///
/// <para>
/// Governance: a definition is created <see cref="IsEnabled"/>=false (Draft). Admin+Operator may
/// edit/delete it <em>while disabled</em>; once an Admin enables it, all mutations become
/// Admin-only — this closes the latest-wins instant-publish gap. <see cref="ScriptTemplate"/> is
/// stored plaintext (like workflow runScript bodies); secrets must come via <c>{{globals.X}}</c> or
/// credentials, never as input fields (there is no secret input type).
/// </para>
/// </summary>
public class CustomActivityDefinition
{
    public Guid Id { get; set; }

    /// <summary>
    /// Stable, <b>immutable</b> slug embedded in the <c>custom:&lt;Key&gt;</c> activity-type string.
    /// Restricted to <c>[A-Za-z0-9_\-]{1,64}</c>. Immutable because a rename would orphan every
    /// node whose persisted type embeds the old key.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Author-chosen Material Symbol name (validated against the known set; default <c>extension</c>).</summary>
    public string Icon { get; set; } = "extension";

    /// <summary>Optional accent colour (hex) driving the node's canvas styling; null = shared custom fallback palette.</summary>
    public string? Color { get; set; }

    /// <summary>The PowerShell template. Resolved inputs are injected as <c>$name</c> variables before this body runs.</summary>
    public string ScriptTemplate { get; set; } = string.Empty;

    /// <summary>auto | pwsh | powershell — forwarded to the execution engine.</summary>
    public string Engine { get; set; } = "auto";

    /// <summary>
    /// When true the node requires a target machine (WinRM); when false the script is forced to run
    /// locally in the API process. The executor enforces this explicitly (routing is otherwise
    /// data-driven and would silently run local on a missing target).
    /// </summary>
    public bool RunsRemote { get; set; }

    /// <summary>Opt-in Windows Job Object isolation for local execution.</summary>
    public bool Isolated { get; set; }
    public int? MemoryLimitMb { get; set; }
    public int? MaxProcesses { get; set; }

    /// <summary>Default per-step timeout (seconds) seeded into a new node's config; null = none.</summary>
    public int? DefaultTimeoutSeconds { get; set; }

    /// <summary>Optional comma-separated exit-code allow-list (e.g. "0,1"); null = pure error-based success.</summary>
    public string? SuccessExitCodes { get; set; }

    /// <summary>
    /// JSON array of <see cref="CustomActivityParameters"/> input descriptors
    /// (name/label/type/required/default/options/description). Parameter names must match
    /// <c>[A-Za-z0-9_]+</c> (PowerShell variable grammar) and be disjoint from output names.
    /// </summary>
    public string InputParametersJson { get; set; } = "[]";

    /// <summary>
    /// JSON array of output descriptors (name/type). The names form the capture allow-list — only
    /// these (plus the always-present <c>exitCode</c>) are surfaced as <c>{{node.param.X}}</c>.
    /// </summary>
    public string OutputParametersJson { get; set; } = "[]";

    /// <summary>Palette visibility / kill switch. Created false (Draft); Admin-only to flip.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Monotonic snapshot counter; matches the latest <see cref="CustomActivityDefinitionVersion"/> row.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Soft-delete tombstone — keeps the script+versions resolvable for old executions / audit.</summary>
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Optimistic-concurrency guard. Regenerated on every mutation; a caller must echo the value it
    /// read, and the store rejects a stale token with a conflict (provider-agnostic alternative to a
    /// SQL rowversion).
    /// </summary>
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Optional human note on the latest change (e.g. "Rolled back to version 3").</summary>
    public string? ChangeNote { get; set; }
}
