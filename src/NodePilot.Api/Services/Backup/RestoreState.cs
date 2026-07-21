using System.Text.Json.Nodes;
using NodePilot.Core.Models;
using NodePilot.Data.Security;

namespace NodePilot.Api.Services.Backup;

/// <summary>
/// Mutable working state for one restore (ADR 0001). Holds the source→target id-maps (K3) built as
/// each section is applied, the by-natural-key lookups + existing-id sets of the target DB (loaded
/// once up front), and the set of source ids present in the backup per type (used by the K12
/// reference-resolvability check). Created before the transaction; mutated as sections restore.
/// </summary>
internal sealed class RestoreState
{
    public BackupFileReader Reader { get; }
    public PassphraseSecretProtector Protector { get; }
    private readonly IReadOnlyDictionary<string, RestoreConflictPolicy> _policies;

    // By-natural-key lookups into the target DB (and freshly-created rows), filled during restore.
    public Dictionary<string, User> Users { get; } = new(StringComparer.Ordinal);
    public Dictionary<Guid, User> UsersById { get; } = [];
    public Dictionary<string, SharedWorkflowFolder> Folders { get; } = new(StringComparer.Ordinal); // by Path
    public Dictionary<string, Credential> Credentials { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ManagedMachine> Machines { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, GlobalVariableFolder> GlobalFolders { get; } = new(StringComparer.Ordinal); // by Path
    public Dictionary<string, GlobalVariable> Globals { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, CustomActivityDefinition> CustomActivities { get; } = new(StringComparer.Ordinal); // by Key
    public Dictionary<string, Workflow> Workflows { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, NotificationRule> NotificationRules { get; } = new(StringComparer.Ordinal); // by Name

    public HashSet<Guid> ExistingUserIds { get; } = [];
    public HashSet<Guid> ExistingFolderIds { get; } = [];
    public HashSet<Guid> ExistingGlobalFolderIds { get; } = [];
    public HashSet<Guid> ExistingCredentialIds { get; } = [];
    public HashSet<Guid> ExistingMachineIds { get; } = [];
    public HashSet<Guid> ExistingCustomActivityIds { get; } = [];
    public HashSet<Guid> ExistingWorkflowIds { get; } = [];
    public HashSet<Guid> ExistingNotificationRuleIds { get; } = [];

    public Dictionary<Guid, Guid> UserMap { get; } = [];
    public Dictionary<Guid, Guid> FolderMap { get; } = [];
    public Dictionary<Guid, Guid> GlobalFolderMap { get; } = [];
    public Dictionary<Guid, Guid> CredentialMap { get; } = [];
    public Dictionary<Guid, Guid> MachineMap { get; } = [];
    public Dictionary<Guid, Guid> CustomActivityMap { get; } = [];
    public Dictionary<Guid, Guid> WorkflowMap { get; } = [];

    public List<string> Warnings { get; } = [];

    private readonly HashSet<Guid> _backupCredentialIds;
    private readonly HashSet<Guid> _backupMachineIds;
    private readonly HashSet<Guid> _backupFolderIds;
    private readonly HashSet<Guid> _backupGlobalFolderIds;

    public RestoreState(BackupFileReader reader, PassphraseSecretProtector protector,
        IReadOnlyDictionary<string, RestoreConflictPolicy> policies)
    {
        Reader = reader;
        Protector = protector;
        _policies = policies;
        _backupCredentialIds = SourceIds(reader, BackupSections.Credentials, "items");
        _backupMachineIds = SourceIds(reader, BackupSections.Machines, "items");
        _backupFolderIds = SourceIds(reader, BackupSections.Folders, "structure");
        _backupGlobalFolderIds = SourceIds(reader, BackupSections.GlobalVariableFolders, "structure");
    }

    public RestoreConflictPolicy Policy(string section) =>
        _policies.TryGetValue(section, out var p) ? p : RestoreConflictPolicy.Skip;

    // ---- resolvability (validation, before any write) ----
    public bool CredentialResolvable(Guid g) => _backupCredentialIds.Contains(g) || ExistingCredentialIds.Contains(g);
    public bool MachineResolvable(Guid g) => _backupMachineIds.Contains(g) || ExistingMachineIds.Contains(g);
    public bool FolderResolvable(Guid g) =>
        g == SharedWorkflowFolder.RootFolderId || _backupFolderIds.Contains(g) || ExistingFolderIds.Contains(g);
    public bool GlobalFolderResolvable(Guid g) =>
        g == GlobalVariableFolder.RootFolderId || _backupGlobalFolderIds.Contains(g) || ExistingGlobalFolderIds.Contains(g);

    // ---- id remap (during write) — mapped, else identity-if-already-present, else null ----
    public Guid? ResolveCredential(Guid g) =>
        CredentialMap.TryGetValue(g, out var t) ? t : ExistingCredentialIds.Contains(g) ? g : null;
    public Guid? ResolveMachine(Guid g) =>
        MachineMap.TryGetValue(g, out var t) ? t : ExistingMachineIds.Contains(g) ? g : null;
    public Guid? ResolveFolder(Guid g) =>
        g == SharedWorkflowFolder.RootFolderId ? SharedWorkflowFolder.RootFolderId
        : FolderMap.TryGetValue(g, out var t) ? t : ExistingFolderIds.Contains(g) ? g : null;
    public Guid? ResolveGlobalFolder(Guid g) =>
        g == GlobalVariableFolder.RootFolderId ? GlobalVariableFolder.RootFolderId
        : GlobalFolderMap.TryGetValue(g, out var t) ? t : ExistingGlobalFolderIds.Contains(g) ? g : null;

    /// <summary>Maps a backed-up custom-activity definition id to its restored target id (for
    /// <c>config.__customDefinitionId</c> in workflow node configs). Null when neither in the backup
    /// nor the target DB — the reference is left as-is and resolves (or fails cleanly) at run time.</summary>
    public Guid? ResolveCustomActivity(Guid g) =>
        CustomActivityMap.TryGetValue(g, out var t) ? t : ExistingCustomActivityIds.Contains(g) ? g : null;

    private static HashSet<Guid> SourceIds(BackupFileReader reader, string section, string arrayKey)
    {
        var set = new HashSet<Guid>();
        if ((reader.Sections[section] as JsonObject)?[arrayKey] is JsonArray arr)
            foreach (var item in arr)
                if (item?["sourceId"]?.GetValue<string>() is { } s && Guid.TryParse(s, out var g))
                    set.Add(g);
        return set;
    }
}
