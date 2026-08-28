namespace NodePilot.Core.Models;

/// <summary>
/// A user-authored, reusable workflow activity backed by a parameterized PowerShell template.
/// Appears in the designer as a "Custom Node" and runs on the shared runScript machinery:
/// engine selection, process isolation, marker-based structured output, secret redaction,
/// exit-code semantics and local/remote routing. It is a reusable runScript preset and adds no
/// second script engine.
///
/// <para>
/// A workflow node references a definition through <c>data.config.__customDefinitionId</c>, the
/// authoritative link, and carries the routing type <c>custom:&lt;Key&gt;</c> as its
/// <c>activityType</c>. The engine resolves any <c>custom:*</c> type to the single
/// <c>CustomActivityExecutor</c>, which loads this row, verifies the key matches, injects the
/// resolved inputs as PowerShell variables and captures only the declared outputs.
/// </para>
///
/// <para>
/// Governance: a definition is created with <see cref="IsEnabled"/>=false (Draft). Admin and
/// Operator may edit or delete it while it is disabled; once an Admin enables it, every mutation
/// is Admin-only. <see cref="ScriptTemplate"/> is stored in plaintext like workflow runScript
/// bodies, so secrets must come from <c>{{globals.X}}</c> or credentials; there is no secret
/// input type.
/// </para>
/// </summary>
public class CustomActivityDefinition
{
    public Guid Id { get; set; }

    /// <summary>
    /// Immutable slug embedded in the <c>custom:&lt;Key&gt;</c> activity-type string, restricted
    /// to <c>[A-Za-z0-9_\-]{1,64}</c>. A rename would orphan every node whose persisted type
    /// embeds the old key.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Author-chosen Material Symbol name, validated against the known set; default
    /// <c>extension</c>.</summary>
    public string Icon { get; set; } = "extension";

    /// <summary>Optional accent color (hex) for the node's canvas styling; null uses the shared
    /// custom fallback palette.</summary>
    public string? Color { get; set; }

    /// <summary>The PowerShell template. Resolved inputs are injected as <c>$name</c> variables
    /// before entering the method body.</summary>
    public string ScriptTemplate { get; set; } = string.Empty;

    /// <summary>auto, pwsh or powershell; forwarded to the execution engine.</summary>
    public string Engine { get; set; } = "auto";

    /// <summary>
    /// When true the node requires a target machine (WinRM); when false the script runs locally
    /// in the API process. The executor enforces this, because the otherwise data-driven routing
    /// would silently run locally when the target is missing.
    /// </summary>
    public bool RunsRemote { get; set; }

    /// <summary>Opt-in Windows Job Object isolation for local execution.</summary>
    public bool Isolated { get; set; }
    public int? MemoryLimitMb { get; set; }
    public int? MaxProcesses { get; set; }

    /// <summary>Per-step timeout (seconds) seeded into a new node's config; null = none.</summary>
    public int? DefaultTimeoutSeconds { get; set; }

    /// <summary>Optional comma-separated exit-code allow-list (for example "0,1"); null means
    /// pure error-based success.</summary>
    public string? SuccessExitCodes { get; set; }

    /// <summary>
    /// JSON array of <see cref="CustomActivityParameters"/> input descriptors
    /// (name/label/type/required/default/options/description). Parameter names must match
    /// <c>[A-Za-z0-9_]+</c> (PowerShell variable grammar) and be disjoint from output names.
    /// </summary>
    public string InputParametersJson { get; set; } = "[]";

    /// <summary>
    /// JSON array of output descriptors (name/type). The names form the capture allow-list: only
    /// these, plus the always-present <c>exitCode</c>, surface as <c>{{node.param.X}}</c>.
    /// </summary>
    public string OutputParametersJson { get; set; } = "[]";

    /// <summary>Palette visibility. Created false (Draft); only an Admin can flip it.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Monotonic snapshot counter; matches the latest
    /// <see cref="CustomActivityDefinitionVersion"/> row.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Soft-delete tombstone; script and versions stay resolvable for past runs.</summary>
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Optimistic-concurrency guard, regenerated on every mutation. A caller must echo the value
    /// it read; the store rejects a stale token with a conflict. Provider-agnostic alternative to
    /// a SQL rowversion.
    /// </summary>
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Optional note on the latest change, e.g. "Rolled back to version 3".</summary>
    public string? ChangeNote { get; set; }
}
