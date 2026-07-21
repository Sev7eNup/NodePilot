namespace NodePilot.Api.Dtos;

/// <summary>Row counts per backupable section — drives the UI checkbox list.</summary>
public sealed record BackupManifestResponse(IReadOnlyList<BackupSectionCountDto> Sections);

public sealed record BackupSectionCountDto(string Section, int Count);

/// <summary>
/// Export request. <see cref="Passphrase"/> travels in the request body over TLS and is never
/// logged or audited. <see cref="Sections"/> lists the desired sections; if a chosen section
/// references another one (e.g. Workflows referencing Machines/Credentials/Folders), that
/// referenced section is auto-included server-side so the resulting backup never has dangling
/// references (see the backup design doc, ADR 0001, decision K12).
/// </summary>
public sealed record BackupExportRequest(List<string> Sections, string Passphrase);
