using System.Text.Json.Nodes;
using NodePilot.Data.Security;

namespace NodePilot.Api.Services.Backup;

/// <summary>
/// Section keys for the <c>nodepilot-system-backup/v1</c> envelope. Stable strings — they appear
/// in the file, the manifest, the CLI <c>--sections</c> flag and the restore conflict-policy map.
/// </summary>
public static class BackupSections
{
    public const string Folders = "folders";
    public const string Users = "users";
    public const string Credentials = "credentials";
    public const string Machines = "machines";
    public const string GlobalVariableFolders = "globalVariableFolders";
    public const string GlobalVariables = "globalVariables";
    public const string CustomActivities = "customActivities";
    public const string Workflows = "workflows";
    public const string Settings = "settings";
    /// <summary>Alerting: custom rules + system policies (both <c>NotificationRule</c> kinds) with routes and
    /// scope targets. Added in schema v2 (ADR 0008). Route secrets are passphrase-rewrapped like credentials;
    /// the delivery ledger / suppression / policy-state are deliberately NOT captured (transient).</summary>
    public const string Alerting = "alerting";

    /// <summary>
    /// Every restorable section. Single source of truth for anything that has to enumerate
    /// sections — notably the restore conflict-policy parser, which used to keep its own
    /// hand-written array. That copy was missing <see cref="GlobalVariableFolders"/>,
    /// <see cref="CustomActivities"/> and <see cref="Alerting"/>, so a global <c>overwrite</c>
    /// never reached them and <c>RestoreState.Policy</c> silently fell back to <c>Skip</c> —
    /// a DR restore that reported success but left alerting rules and custom activities
    /// untouched.
    /// </summary>
    public static readonly string[] All =
    [
        Folders, Users, Credentials, Machines, GlobalVariableFolders,
        GlobalVariables, CustomActivities, Workflows, Settings, Alerting,
    ];

    /// <summary>Original envelope schema (pre-alerting). Still importable. Also the stable KDF label the
    /// passphrase verifier token is derived from — kept fixed across versions so both v1 and v2 backups
    /// verify with the same passphrase machinery.</summary>
    public const string Schema = "nodepilot-system-backup/v1";
    /// <summary>Current envelope schema — adds the <see cref="Alerting"/> section. New exports write this.</summary>
    public const string SchemaV2 = "nodepilot-system-backup/v2";
    /// <summary>
    /// Protects each complete workflow definition in a passphrase envelope. Older readers must
    /// reject this schema instead of treating <c>$encDefinition</c> as an ordinary definition.
    /// </summary>
    public const string SchemaV3 = "nodepilot-system-backup/v3";
    /// <summary>The schema every new export writes.</summary>
    public const string CurrentSchema = SchemaV3;
    /// <summary>Schemas this build can import. Older builds reject unknown newer schemas visibly.</summary>
    public static readonly string[] SupportedSchemas = [Schema, SchemaV2, SchemaV3];
}

/// <summary>
/// Per-export state threaded to every <see cref="IBackupPart"/>. Carries the passphrase protector
/// (for the <c>$enc</c> fields + later the whole-file MAC) and a warning sink so a part can report
/// non-fatal issues (e.g. a credential whose at-rest ciphertext could not be decrypted on this host).
/// </summary>
public sealed class BackupExportContext
{
    public required PassphraseSecretProtector Protector { get; init; }

    private readonly List<string> _warnings = [];
    public IReadOnlyList<string> Warnings => _warnings;
    public void Warn(string message) => _warnings.Add(message);

    /// <summary>Wraps a plaintext as an <c>{"$enc":"&lt;base64&gt;"}</c> node under the backup passphrase.</summary>
    public JsonObject Enc(string plaintext) => new()
    {
        [WorkflowDefinitionSecretRewriter.EncKey] = Convert.ToBase64String(Protector.Protect(plaintext)),
    };
}

/// <summary>
/// One backupable resource type. Implementations are scoped services (they hold a scoped
/// <c>DbContext</c> + stores). Phase 1 implements <see cref="CountAsync"/> + <see cref="ExportAsync"/>;
/// Preview/Restore arrive in Phase 2.
/// </summary>
public interface IBackupPart
{
    /// <summary>Stable section key (see <see cref="BackupSections"/>).</summary>
    string Key { get; }

    /// <summary>
    /// Other section keys this part's data references (ADR 0001 K12). When the caller selects this
    /// section, the export auto-includes the transitive closure of these so references stay resolvable.
    /// </summary>
    IReadOnlyList<string> DependsOn { get; }

    /// <summary>Row count for the manifest (drives the UI checkbox labels).</summary>
    Task<int> CountAsync(CancellationToken ct);

    /// <summary>Produces this section's payload node for the envelope.</summary>
    Task<JsonNode> ExportAsync(BackupExportContext ctx, CancellationToken ct);
}
