namespace NodePilot.Api.Services.Backup;

/// <summary>
/// How a restore treats an item whose natural key already exists in the target DB.
/// </summary>
public enum RestoreConflictPolicy
{
    /// <summary>Keep the existing row untouched; map the backup id onto it. Default.</summary>
    Skip,

    /// <summary>
    /// Create the backup row under a suffixed name, leaving the existing one intact.
    /// </summary>
    Rename,

    /// <summary>Update the existing row from the backup.</summary>
    Overwrite,
}

/// <summary>
/// Per-section preview diff from an authenticated, successfully decrypted backup (K10).
/// </summary>
public sealed record BackupPreviewSection(string Section, int InBackup, int New, int Conflicts);

/// <summary>
/// Preview of what a restore would do. A successful v4 preview has already authenticated and
/// decrypted the complete payload, so <see cref="IntegrityVerified"/> is true (K5/K10).
/// </summary>
public sealed record BackupPreviewResult(
    bool IntegrityVerified,
    string? AppVersion,
    IReadOnlyList<BackupPreviewSection> Sections,
    IReadOnlyList<string> Warnings);

/// <summary>Outcome for one restored section.</summary>
public sealed record SectionRestoreResult(
    string Section, int Created, int Overwritten, int Skipped, int Renamed);

/// <summary>Settings-file restore is reported separately and compensated if the DB commit fails (K8).</summary>
public sealed record SettingsRestoreResult(bool Applied, string? Message);

/// <summary>Full restore outcome.</summary>
public sealed record BackupRestoreResult(
    IReadOnlyList<SectionRestoreResult> Sections,
    SettingsRestoreResult? Settings,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Raised when a restore must abort (wrong passphrase, failed authentication, unresolvable refs, last-admin).
/// </summary>
public sealed class BackupRestoreException(string message) : Exception(message);
