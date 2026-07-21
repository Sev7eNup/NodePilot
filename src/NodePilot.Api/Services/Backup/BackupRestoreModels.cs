namespace NodePilot.Api.Services.Backup;

/// <summary>How a restore treats an item whose natural key already exists in the target DB.</summary>
public enum RestoreConflictPolicy
{
    /// <summary>Keep the existing row untouched; map the backup id onto it. Default.</summary>
    Skip,

    /// <summary>Create the backup row under a suffixed name, leaving the existing one intact.</summary>
    Rename,

    /// <summary>Update the existing row from the backup.</summary>
    Overwrite,
}

/// <summary>Per-section preview diff. Without a passphrase, secrets are not compared (K10).</summary>
public sealed record BackupPreviewSection(string Section, int InBackup, int New, int Conflicts);

/// <summary>
/// Preview of what a restore would do. <see cref="IntegrityVerified"/> is false when no passphrase
/// was supplied (status <c>integrityUnverified</c>, K5/K10) — counts/names are still shown.
/// </summary>
public sealed record BackupPreviewResult(
    bool IntegrityVerified,
    string? AppVersion,
    IReadOnlyList<BackupPreviewSection> Sections,
    IReadOnlyList<string> Warnings);

/// <summary>Outcome for one restored section.</summary>
public sealed record SectionRestoreResult(
    string Section, int Created, int Overwritten, int Skipped, int Renamed);

/// <summary>Settings restore is non-transactional and reported separately (K8).</summary>
public sealed record SettingsRestoreResult(bool Applied, string? Message);

/// <summary>Full restore outcome.</summary>
public sealed record BackupRestoreResult(
    IReadOnlyList<SectionRestoreResult> Sections,
    SettingsRestoreResult? Settings,
    IReadOnlyList<string> Warnings);

/// <summary>Raised when a restore must abort (wrong passphrase, failed MAC, unresolvable refs, last-admin).</summary>
public sealed class BackupRestoreException(string message) : Exception(message);
