using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NodePilot.Data.Security;

namespace NodePilot.Api.Services.Backup;

/// <summary>One section's row count, for the manifest.</summary>
public sealed record BackupSectionCount(string Section, int Count);

/// <summary>Manifest of what a backup could contain (drives the UI checkbox list).</summary>
public sealed record BackupManifest(IReadOnlyList<BackupSectionCount> Sections);

/// <summary>Result of an export: the file bytes plus operator-facing metadata.</summary>
public sealed record BackupExportResult(
    byte[] Content,
    IReadOnlyList<string> IncludedSections,
    IReadOnlyList<string> AutoIncludedSections,
    IReadOnlyList<string> Warnings,
    bool ContainsSecrets);

/// <summary>
/// Orchestrates the system-configuration backup (ADR 0001). Phase 1 covers the manifest and the
/// export: it resolves the requested sections + their transitive dependencies (K12), drives each
/// <see cref="IBackupPart"/>, assembles the <c>nodepilot-system-backup/v1</c> envelope, and seals it
/// with a passphrase-derived whole-file HMAC (K5).
/// </summary>
public sealed class BackupService(IEnumerable<IBackupPart> parts)
{
    /// <summary>Minimum backup passphrase length. A backup is a portable secret-bearing artifact.</summary>
    public const int MinPassphraseLength = 12;

    // Stable export/manifest order (mirrors the restore order in ADR 0001 K4 for readability).
    private static readonly string[] SectionOrder =
    [
        BackupSections.Folders, BackupSections.Users, BackupSections.Credentials,
        BackupSections.Machines, BackupSections.GlobalVariableFolders, BackupSections.GlobalVariables,
        BackupSections.CustomActivities, BackupSections.Workflows, BackupSections.Alerting, BackupSections.Settings,
    ];

    private IReadOnlyDictionary<string, IBackupPart> PartsByKey =>
        parts.ToDictionary(p => p.Key, StringComparer.Ordinal);

    public async Task<BackupManifest> GetManifestAsync(CancellationToken ct)
    {
        var byKey = PartsByKey;
        var counts = new List<BackupSectionCount>();
        foreach (var key in SectionOrder)
        {
            if (!byKey.TryGetValue(key, out var part)) continue;
            counts.Add(new BackupSectionCount(key, await part.CountAsync(ct)));
        }
        return new BackupManifest(counts);
    }

    public async Task<BackupExportResult> ExportAsync(
        IEnumerable<string> requestedSections, string passphrase, string? createdBy, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < MinPassphraseLength)
            throw new ArgumentException(
                $"Backup passphrase must be at least {MinPassphraseLength} characters.", nameof(passphrase));

        var byKey = PartsByKey;
        var requested = new HashSet<string>(requestedSections, StringComparer.Ordinal);
        var unknown = requested.Where(s => !byKey.ContainsKey(s)).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"Unknown backup section(s): {string.Join(", ", unknown)}.", nameof(requestedSections));
        if (requested.Count == 0)
            throw new ArgumentException("At least one section must be selected.", nameof(requestedSections));

        // K12 — pull the transitive closure of hard dependencies so references stay resolvable.
        var effective = new HashSet<string>(requested, StringComparer.Ordinal);
        var queue = new Queue<string>(requested);
        while (queue.Count > 0)
        {
            var key = queue.Dequeue();
            foreach (var dep in byKey[key].DependsOn)
                if (effective.Add(dep)) queue.Enqueue(dep);
        }
        var autoIncluded = effective.Except(requested).OrderBy(s => s, StringComparer.Ordinal).ToList();

        var salt = PassphraseSecretProtector.GenerateSalt();
        var protector = PassphraseSecretProtector.Derive(passphrase, salt);
        var ctx = new BackupExportContext { Protector = protector };

        var sections = new JsonObject();
        var includedOrdered = SectionOrder.Where(effective.Contains).ToList();
        foreach (var key in includedOrdered)
            sections[key] = await byKey[key].ExportAsync(ctx, ct);

        var envelope = new JsonObject
        {
            ["schema"] = BackupSections.CurrentSchema,
            ["appVersion"] = AppVersion(),
            ["createdAt"] = DateTime.UtcNow.ToString("O"),
            ["createdBy"] = createdBy,
            ["crypto"] = new JsonObject
            {
                ["kdf"] = PassphraseSecretProtector.KdfName,
                ["iterations"] = PassphraseSecretProtector.DefaultIterations,
                ["salt"] = Convert.ToBase64String(salt),
                ["verifier"] = Convert.ToBase64String(protector.CreateVerifier()),
            },
            ["sections"] = sections,
        };

        // Seal: HMAC over the canonical envelope (excluding the mac field itself), then embed it.
        var canonical = BackupCanonicalJson.Canonicalize(envelope, excludeKey: "mac");
        envelope["mac"] = Convert.ToBase64String(protector.ComputeMac(canonical));

        var json = envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var content = Encoding.UTF8.GetBytes(json);

        // Accurate audit signal: a backup "contains secrets" only if at least one field was actually
        // sealed (the $enc marker). A globals-only export with no secret variables, for example,
        // legitimately carries none — reporting true unconditionally would muddy the audit trail.
        var containsSecrets = sections.ToJsonString()
            .Contains("\"" + WorkflowDefinitionSecretRewriter.EncKey + "\"", StringComparison.Ordinal);

        return new BackupExportResult(content, includedOrdered, autoIncluded, ctx.Warnings, containsSecrets);
    }

    private static string AppVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(BackupService).Assembly;
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}
