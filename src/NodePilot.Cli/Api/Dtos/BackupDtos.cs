namespace NodePilot.Cli.Api.Dtos;

// Mirrors src/NodePilot.Api/Dtos/BackupDtos.cs (see ADR 0001 — the system-config-backup/restore design).
public sealed record BackupManifestResponse(List<BackupSectionCountDto> Sections);
public sealed record BackupSectionCountDto(string Section, int Count);
public sealed record BackupExportRequest(List<string> Sections, string Passphrase);

public sealed record BackupPreviewSection(string Section, int InBackup, int New, int Conflicts);
public sealed record BackupPreviewResult(bool IntegrityVerified, string? AppVersion, List<BackupPreviewSection> Sections, List<string> Warnings);

public sealed record SectionRestoreResult(string Section, int Created, int Overwritten, int Skipped, int Renamed);
public sealed record SettingsRestoreResult(bool Applied, string? Message);
public sealed record BackupRestoreResult(List<SectionRestoreResult> Sections, SettingsRestoreResult? Settings, List<string> Warnings);
