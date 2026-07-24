using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NodePilot.Api.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.Services.Backup;
using NodePilot.Core.Audit;

namespace NodePilot.Api.Controllers;

/// <summary>
/// System-configuration backup (ADR 0001). Admin-only — an export reads every secret-bearing
/// resource (credentials, secret globals, user password hashes) and seals them behind a passphrase.
/// Phase 1 exposes the manifest and the export; preview/restore arrive in Phase 2.
/// </summary>
[ApiController]
[Route("api/backup")]
[Authorize(Roles = "Admin")]
[EnableRateLimiting("backup")]
public sealed class BackupController : ControllerBase
{
    private readonly BackupService _backup;
    private readonly BackupRestoreService _restore;
    private readonly IAuditWriter _audit;
    private readonly ILogger<BackupController> _logger;

    public BackupController(BackupService backup, BackupRestoreService restore, IAuditWriter audit, ILogger<BackupController> logger)
    {
        _backup = backup;
        _restore = restore;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>What a backup could contain, with row counts per section.</summary>
    [HttpGet("manifest")]
    public async Task<ActionResult<BackupManifestResponse>> Manifest(CancellationToken ct)
    {
        var manifest = await _backup.GetManifestAsync(ct);
        return Ok(new BackupManifestResponse(
            manifest.Sections.Select(s => new BackupSectionCountDto(s.Section, s.Count)).ToList()));
    }

    /// <summary>
    /// Produces a sealed <c>.npbackup</c> file for the selected sections. The passphrase is never
    /// logged; the audit row records only which sections were exported and how many secrets.
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export(BackupExportRequest request, CancellationToken ct)
    {
        if (request is null || request.Sections is null)
            return BadRequest(new { error = "Sections are required." });

        BackupExportResult result;
        try
        {
            result = await _backup.ExportAsync(request.Sections, request.Passphrase ?? "", User.Identity?.Name, ct);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        await _audit.LogAsync(AuditActions.BackupExported, "Backup", null,
            AuditDetails.Json(
                ("sections", string.Join(",", result.IncludedSections)),
                ("autoIncluded", string.Join(",", result.AutoIncludedSections)),
                ("counts", string.Join(",", result.Counts.Select(count => $"{count.Section}:{count.Count}"))),
                ("containsSecrets", result.ContainsSecrets.ToString()),
                ("warnings", result.Warnings.Count.ToString())),
            ct);

        if (result.Warnings.Count > 0)
            _logger.LogWarning("Backup export completed with {Count} warning(s): {Warnings}",
                result.Warnings.Count, string.Join(" | ", result.Warnings));

        var filename = $"nodepilot-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.npbackup";
        // Surface non-fatal warnings (e.g. undecryptable credential) in a response header so the
        // CLI/UI can show them — the body is the raw file stream.
        if (result.Warnings.Count > 0)
            Response.Headers["X-Backup-Warnings"] = result.Warnings.Count.ToString();
        return File(result.Content, "application/octet-stream", filename);
    }

    /// <summary>
    /// Dry-run a restore: reports per-section new/conflict counts without writing. Passphrase is
    /// optional — without it the result is <c>integrityUnverified</c> and secrets are not compared (K10).
    /// </summary>
    [HttpPost("preview")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    public async Task<ActionResult<BackupPreviewResult>> Preview(
        IFormFile file, [FromForm] string? passphrase, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Backup file is required." });
        var bytes = await ReadAllAsync(file, ct);
        try
        {
            return Ok(await _restore.PreviewAsync(bytes, passphrase, ct));
        }
        catch (BackupFormatException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (IsMalformedContent(ex))
        {
            // Security: don't append raw ex.Message — a malformed file surfaces parser / crypto
            // internals. The curated reason is enough; full detail goes to the server log.
            _logger.LogWarning(ex, "Backup preview rejected malformed content");
            return BadRequest(new { error = "Malformed backup content." });
        }
    }

    /// <summary>
    /// Applies a restore. Requires the passphrase, verifies the whole-file MAC, validates references,
    /// and writes all DB sections in one transaction (settings applied separately afterwards, K8).
    /// </summary>
    [HttpPost("restore")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    public async Task<ActionResult<BackupRestoreResult>> Restore(
        IFormFile file, [FromForm] string passphrase, [FromForm] string? policy, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Backup file is required." });
        if (string.IsNullOrEmpty(passphrase)) return BadRequest(new { error = "Passphrase is required for restore." });

        var bytes = await ReadAllAsync(file, ct);
        var policies = ParsePolicies(policy);
        BackupRestoreResult result;
        try
        {
            result = await _restore.RestoreAsync(bytes, passphrase, policies, ct);
        }
        catch (BackupFormatException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (BackupRestoreException ex)
        {
            // Conflict — wrong passphrase / failed MAC / unresolvable refs / last-admin guard.
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex) when (IsMalformedContent(ex))
        {
            // A MAC-valid but structurally malformed file (e.g. a hand-edited + re-signed backup
            // with a non-GUID id) must surface as a clean 400, not a 500. Security: don't append
            // raw ex.Message — it surfaces parser / crypto internals; detail goes to the log.
            _logger.LogWarning(ex, "Backup restore rejected malformed content");
            return BadRequest(new { error = "Malformed backup content." });
        }

        await _audit.LogAsync(AuditActions.BackupRestored, "Backup", null,
            AuditDetails.Json(
                ("sections", string.Join(",", result.Sections.Select(r => $"{r.Section}:+{r.Created}/~{r.Overwritten}/={r.Skipped}/»{r.Renamed}"))),
                ("policies", string.Join(",", result.Sections.Select(section =>
                    $"{section.Section}:{policies.GetValueOrDefault(section.Section, RestoreConflictPolicy.Skip)}"))),
                ("settings", result.Settings?.Applied.ToString() ?? "n/a"),
                ("warnings", result.Warnings.Count.ToString())),
            ct);

        if (result.Warnings.Count > 0)
            _logger.LogWarning("Backup restore completed with {Count} warning(s): {Warnings}",
                result.Warnings.Count, string.Join(" | ", result.Warnings));
        return Ok(result);
    }

    // Exceptions that indicate the (MAC-verified) file's JSON is structurally wrong — a bad GUID,
    // wrong value kind, unknown enum, etc. — rather than a server fault. Mapped to 400.
    private static bool IsMalformedContent(Exception ex) =>
        ex is FormatException or System.Text.Json.JsonException or ArgumentException
            or InvalidOperationException or OverflowException;

    private static async Task<byte[]> ReadAllAsync(IFormFile file, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    /// <summary>
    /// Parses the policy string: comma-separated <c>section=policy</c> pairs, plus an optional bare
    /// token (e.g. <c>overwrite</c>) that sets the default for every section. Unknown → Skip.
    /// </summary>
    private static Dictionary<string, RestoreConflictPolicy> ParsePolicies(string? policy)
    {
        var result = new Dictionary<string, RestoreConflictPolicy>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(policy)) return result;

        var allSections = new[]
        {
            BackupSections.Folders, BackupSections.Users, BackupSections.Credentials,
            BackupSections.Machines, BackupSections.GlobalVariables, BackupSections.Workflows,
            BackupSections.Settings,
        };
        foreach (var token in policy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = token.IndexOf('=');
            if (eq < 0)
            {
                if (Enum.TryParse<RestoreConflictPolicy>(token, ignoreCase: true, out var global))
                    foreach (var s in allSections) result[s] = global;
                continue;
            }
            var key = token[..eq].Trim();
            if (Enum.TryParse<RestoreConflictPolicy>(token[(eq + 1)..].Trim(), ignoreCase: true, out var p))
                result[key] = p;
        }
        return result;
    }
}
