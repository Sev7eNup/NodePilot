using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Configuration;
using NodePilot.Api.Security;
using NodePilot.Api.Services.Backup;
using NodePilot.Api.Services.Backup.Parts;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security;

/// <summary>
/// The unattended provisioning path: a fresh machine restores a configuration backup at first
/// start, so the rollout ends with users, workflows and settings already present and never opens a
/// bootstrap window. Every test here is about a boundary that decides whether an operator ends up
/// with a working instance, a locked one, or a silently empty one.
/// </summary>
public sealed class ProvisioningSeederTests : IDisposable
{
    private const string Passphrase = "a-strong-backup-pass";
    private readonly AesGcmSecretProtector _atRest = new(Key());
    private readonly List<string> _tempFiles = [];

    private static byte[] Key()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 11);
        return key;
    }

    private string TempPath(string extension = ".json")
    {
        var path = Path.Combine(Path.GetTempPath(), "np-seed-test-" + Guid.NewGuid().ToString("N") + extension);
        _tempFiles.Add(path);
        return path;
    }

    private BackupRestoreService Restore(NodePilotDbContext db) =>
        new(db, _atRest,
            new RuntimeOverridesWriter(TempPath(), NullLogger<RuntimeOverridesWriter>.Instance),
            NullLogger<BackupRestoreService>.Instance,
            new NodePilot.Api.Services.WorkflowVersionDefinitionProtector(
                _atRest, NullLogger<NodePilot.Api.Services.WorkflowVersionDefinitionProtector>.Instance));

    /// <summary>A backup carrying one break-glass Admin — the minimum a seed must contain.</summary>
    private async Task<byte[]> BuildSeedAsync(string username = "seeded-admin")
    {
        using var source = TestDbFactory.Create();
        source.Users.Add(new User
        {
            Id = Guid.NewGuid(), Username = username, Role = UserRole.Admin,
            PasswordHash = "$2a$hash", IsActive = true, IsBreakGlass = true,
        });
        await source.SaveChangesAsync();

        var export = await new BackupService([new UserBackupPart(source)])
            .ExportAsync([BackupSections.Users], Passphrase, "admin", CancellationToken.None);
        return export.Content;
    }

    private static IConfiguration Config(string? path, string? passphrase)
    {
        var values = new Dictionary<string, string?>();
        if (path is not null) values[ProvisioningSeeder.PathKey] = path;
        if (passphrase is not null) values[ProvisioningSeeder.PassphraseKey] = passphrase;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public async Task NoSeedConfigured_DoesNothing()
    {
        using var db = TestDbFactory.Create();

        var seeded = await ProvisioningSeeder.SeedIfEmptyAsync(
            db, Config(null, null), Restore(db), NullLogger.Instance);

        seeded.Should().BeFalse();
        (await db.Users.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task EmptyDatabase_RestoresTheUsersFromTheBackup()
    {
        var seedFile = TempPath(".npbackup");
        await File.WriteAllBytesAsync(seedFile, await BuildSeedAsync());

        using var db = TestDbFactory.Create();
        var seeded = await ProvisioningSeeder.SeedIfEmptyAsync(
            db, Config(seedFile, Passphrase), Restore(db), NullLogger.Instance);

        seeded.Should().BeTrue();
        var user = await db.Users.SingleAsync();
        user.Username.Should().Be("seeded-admin");
        user.Role.Should().Be(UserRole.Admin);
        // The property the whole enterprise story hangs on: EnterpriseRecoveryInvariant refuses to
        // start with SSO enabled unless a local break-glass Admin exists, and external JIT
        // provisioning is blocked until one does. A seed that lost this flag would produce an
        // instance that cannot enable the very thing it was provisioned for.
        user.IsBreakGlass.Should().BeTrue();
    }

    [Fact]
    public async Task SeedIsConsumed_AndTheFileIsRemoved()
    {
        var seedFile = TempPath(".npbackup");
        await File.WriteAllBytesAsync(seedFile, await BuildSeedAsync());

        using var db = TestDbFactory.Create();
        await ProvisioningSeeder.SeedIfEmptyAsync(
            db, Config(seedFile, Passphrase), Restore(db), NullLogger.Instance);

        // It holds every credential the reference machine had. Leaving it on each rolled-out
        // machine would multiply that exposure by the size of the estate.
        File.Exists(seedFile).Should().BeFalse();
    }

    [Fact]
    public async Task PopulatedDatabase_IsNeverTouched()
    {
        var seedFile = TempPath(".npbackup");
        await File.WriteAllBytesAsync(seedFile, await BuildSeedAsync());

        using var db = TestDbFactory.Create();
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(), Username = "already-here", Role = UserRole.Admin,
            PasswordHash = "$2a$hash", IsActive = true, IsBreakGlass = true,
        });
        await db.SaveChangesAsync();

        var seeded = await ProvisioningSeeder.SeedIfEmptyAsync(
            db, Config(seedFile, Passphrase), Restore(db), NullLogger.Instance);

        // The guard that makes this safe to leave configured forever: a seed is a first fill, never
        // a migration. An instance in service keeps everything it has, whatever the config says.
        seeded.Should().BeFalse();
        (await db.Users.SingleAsync()).Username.Should().Be("already-here");
        File.Exists(seedFile).Should().BeTrue("an untouched instance must not consume the seed either");
    }

    [Fact]
    public async Task WrongPassphrase_FailsClosed()
    {
        var seedFile = TempPath(".npbackup");
        await File.WriteAllBytesAsync(seedFile, await BuildSeedAsync());

        using var db = TestDbFactory.Create();
        var act = () => ProvisioningSeeder.SeedIfEmptyAsync(
            db, Config(seedFile, "not-the-passphrase"), Restore(db), NullLogger.Instance);

        // Starting anyway would leave an empty instance with an open bootstrap window that the
        // operator believes is provisioned — strictly worse than a service that refuses to start.
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*could not be restored*");
        (await db.Users.AnyAsync()).Should().BeFalse("a failed restore must leave no partial state");
    }

    [Fact]
    public async Task MissingSeedFile_FailsClosed()
    {
        using var db = TestDbFactory.Create();
        var act = () => ProvisioningSeeder.SeedIfEmptyAsync(
            db, Config(Path.Combine(Path.GetTempPath(), "does-not-exist.npbackup"), Passphrase),
            Restore(db), NullLogger.Instance);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*does not exist*");
    }

    [Fact]
    public async Task MissingPassphrase_FailsClosed()
    {
        var seedFile = TempPath(".npbackup");
        await File.WriteAllBytesAsync(seedFile, await BuildSeedAsync());

        using var db = TestDbFactory.Create();
        var act = () => ProvisioningSeeder.SeedIfEmptyAsync(
            db, Config(seedFile, passphrase: null), Restore(db), NullLogger.Instance);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*SeedBackupPassphrase*");
    }

    [Fact]
    public async Task AfterSeeding_NoBootstrapTokenIsWritten()
    {
        var seedFile = TempPath(".npbackup");
        await File.WriteAllBytesAsync(seedFile, await BuildSeedAsync());

        using var db = TestDbFactory.Create();
        await ProvisioningSeeder.SeedIfEmptyAsync(
            db, Config(seedFile, Passphrase), Restore(db), NullLogger.Instance);

        // The rule the unattended setup relies on: the presence of the token file is the signal
        // that something still needs bootstrapping. A seeded instance must not produce one, or the
        // adapter would try to redeem a token on a machine that already has its users.
        var usersExist = await db.Users.AnyAsync();
        usersExist.Should().BeTrue();

        var contentRoot = Path.Combine(Path.GetTempPath(), "np-seed-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        _tempFiles.Add(contentRoot);
        var env = new TestHostEnvironment { ContentRootPath = contentRoot, EnvironmentName = "Production" };

        AdminBootstrap.EnsureBootstrapTokenIfNeeded(env, usersExist, NullLogger.Instance);

        File.Exists(Path.Combine(contentRoot, AdminBootstrap.TokenFileName)).Should().BeFalse();
    }

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "NodePilot.Api.Tests";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
