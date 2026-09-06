using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Services.Backup;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>
/// Fills a brand-new installation from a configuration backup before anything else looks at the
/// database, so an unattended rollout comes up with its users, workflows, machines and settings
/// already in place and never opens a bootstrap window at all.
/// </summary>
/// <remarks>
/// This is the one path that can populate an instance without an authenticated caller, and it is
/// deliberately narrow. <c>POST /api/backup/restore</c> is Admin-only, which on an empty instance
/// is a chicken-and-egg problem; the service behind it is not, because
/// <see cref="BackupRestoreService"/> depends on nothing but the database, the secret protector and
/// the runtime-overrides writer. Restoring into an empty database is a case it already anticipates:
/// it requires the restored set to contain a break-glass administrator, so a seed cannot produce an
/// instance nobody can log into.
///
/// External authentication cannot substitute for this. LDAP, Windows SSO and OIDC all refuse
/// just-in-time provisioning until a local break-glass Admin exists, and
/// <see cref="EnterpriseRecoveryInvariant"/> refuses to start at all when SSO is enabled without
/// one. A seeded backup satisfies both, which is what makes SSO usable on a fresh machine.
/// </remarks>
public static class ProvisioningSeeder
{
    public const string PathKey = "Provisioning:SeedBackupPath";
    public const string PassphraseKey = "Provisioning:SeedBackupPassphrase";

    /// <summary>
    /// Restores the configured seed when the instance has no users yet. Returns true when a
    /// restore actually happened, so the caller can re-evaluate whether a bootstrap token is
    /// still needed.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The seed is configured but unusable. Deliberately fatal: the alternative is an empty
    /// instance with an open bootstrap window that the operator believes is provisioned.
    /// </exception>
    public static async Task<bool> SeedIfEmptyAsync(
        NodePilotDbContext db,
        IConfiguration configuration,
        BackupRestoreService restore,
        ILogger logger,
        CancellationToken ct = default)
    {
        var path = configuration[PathKey];
        if (string.IsNullOrWhiteSpace(path)) return false;

        // The only guard that matters. A seed is a first-fill, never a migration: an instance that
        // already has users keeps everything it has, whatever the configuration still says.
        if (await db.Users.AnyAsync(ct))
        {
            logger.LogInformation(
                "Provisioning seed skipped: this instance already has users, so '{Path}' was not read.",
                path);
            return false;
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Provisioning seed '{path}' is configured but does not exist. Refusing to start: " +
                "an operator who configured a seed is expecting a provisioned instance, not an empty " +
                $"one with an open bootstrap window. Remove {PathKey} to start without it.");
        }

        var passphrase = configuration[PassphraseKey];
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            throw new InvalidOperationException(
                $"Provisioning seed '{path}' is configured but {PassphraseKey} is empty. The backup " +
                "cannot be unlocked, and starting without it would leave an empty instance behind.");
        }

        var content = await File.ReadAllBytesAsync(path, ct);

        // Skip everywhere. The target is empty, so no rule can fire; and if that assumption is ever
        // wrong, Skip is the policy that changes the least.
        var policies = BackupSections.All.ToDictionary(
            section => section,
            _ => RestoreConflictPolicy.Skip,
            StringComparer.OrdinalIgnoreCase);

        BackupRestoreResult result;
        try
        {
            // No principal: this runs at first boot, before anyone has logged in. A restored
            // workflow that is enabled therefore needs one Publish before its triggers can fire.
            result = await restore.RestoreAsync(content, passphrase, policies, restoredByUserId: null, ct);
        }
        catch (BackupRestoreException ex)
        {
            throw new InvalidOperationException(
                $"Provisioning seed '{path}' could not be restored: {ex.Message}", ex);
        }

        foreach (var section in result.Sections)
        {
            logger.LogInformation(
                "Provisioning seed restored {Section}: {Created} created, {Skipped} skipped.",
                section.Section, section.Created, section.Skipped);
        }
        foreach (var warning in result.Warnings)
        {
            logger.LogWarning("Provisioning seed warning: {Warning}", warning);
        }

        // The seed carries credentials in it and has no reason to outlive the machine's first
        // start. Failing to remove it must not take the instance down with it - the restore has
        // already succeeded and the database is populated - but it is worth shouting about.
        try
        {
            File.Delete(path);
            logger.LogInformation("Provisioning seed consumed and removed from '{Path}'.", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                "Provisioning seed at '{Path}' was restored but could not be deleted: {Message}. " +
                "It contains credentials — remove it by hand.", path, ex.Message);
        }

        return true;
    }
}
