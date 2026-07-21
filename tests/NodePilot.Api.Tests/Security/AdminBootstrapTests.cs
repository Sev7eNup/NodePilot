using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public sealed class AdminBootstrapTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IHostEnvironment _env;

    public AdminBootstrapTests()
    {
        var basePath = OperatingSystem.IsWindows()
            ? Path.GetPathRoot(Path.GetTempPath())
                ?? throw new InvalidOperationException("Temporary path has no drive root.")
            : Path.GetTempPath();
        _tempDir = Path.Combine(basePath, "AdminBootstrapTests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);

        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Current Windows identity has no SID.");
            var acl = new DirectorySecurity();
            acl.SetOwner(owner);
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(_tempDir).SetAccessControl(acl);
        }
        else
        {
            File.SetUnixFileMode(_tempDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var mock = new Mock<IHostEnvironment>();
        mock.SetupGet(e => e.ContentRootPath).Returns(_tempDir);
        mock.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);
        _env = mock.Object;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string TokenPath => Path.Combine(_tempDir, AdminBootstrap.TokenFileName);

    [Fact]
    public void EnsureBootstrapTokenIfNeeded_NoUsers_CreatesTokenFile()
    {
        var path = AdminBootstrap.EnsureBootstrapTokenIfNeeded(_env, usersExist: false, NullLogger.Instance);
        path.Should().NotBeNull();
        File.Exists(TokenPath).Should().BeTrue();
    }

    [Fact]
    public void EnsureBootstrapTokenIfNeeded_UsersExist_ReturnsNull()
    {
        var path = AdminBootstrap.EnsureBootstrapTokenIfNeeded(_env, usersExist: true, NullLogger.Instance);
        path.Should().BeNull();
    }

    [Fact]
    public void EnsureBootstrapTokenIfNeeded_UsersExist_DeletesStaleTokenFile()
    {
        File.WriteAllText(TokenPath, "old-token");
        AdminBootstrap.EnsureBootstrapTokenIfNeeded(_env, usersExist: true, NullLogger.Instance);
        File.Exists(TokenPath).Should().BeFalse();
    }

    [Fact]
    public void EnsureBootstrapTokenIfNeeded_NoUsers_TokenIsBase64()
    {
        AdminBootstrap.EnsureBootstrapTokenIfNeeded(_env, usersExist: false, NullLogger.Instance);
        var token = File.ReadAllText(TokenPath);
        var act = () => Convert.FromBase64String(token.Trim());
        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureBootstrapTokenIfNeeded_CalledTwice_TokenUnchanged()
    {
        AdminBootstrap.EnsureBootstrapTokenIfNeeded(_env, usersExist: false, NullLogger.Instance);
        var first = File.ReadAllText(TokenPath);
        AdminBootstrap.EnsureBootstrapTokenIfNeeded(_env, usersExist: false, NullLogger.Instance);
        var second = File.ReadAllText(TokenPath);
        second.Should().Be(first);
    }

    [Fact]
    public void Validate_CorrectToken_ReturnsTrue()
    {
        AdminBootstrap.EnsureBootstrapTokenIfNeeded(_env, usersExist: false, NullLogger.Instance);
        var token = File.ReadAllText(TokenPath).Trim();
        AdminBootstrap.Validate(_env, token).Should().BeTrue();
    }

    [Fact]
    public void Validate_WrongToken_ReturnsFalse()
    {
        AdminBootstrap.EnsureBootstrapTokenIfNeeded(_env, usersExist: false, NullLogger.Instance);
        AdminBootstrap.Validate(_env, "wrong-token").Should().BeFalse();
    }

    [Fact]
    public void Validate_NullToken_ReturnsFalse()
    {
        AdminBootstrap.EnsureBootstrapTokenIfNeeded(_env, usersExist: false, NullLogger.Instance);
        AdminBootstrap.Validate(_env, null).Should().BeFalse();
    }

    [Fact]
    public void Validate_NoTokenFile_ReturnsFalse()
    {
        // No call to EnsureBootstrapTokenIfNeeded → file does not exist
        AdminBootstrap.Validate(_env, "any-token").Should().BeFalse();
    }

    [Fact]
    public void Consume_DeletesTokenFile()
    {
        AdminBootstrap.EnsureBootstrapTokenIfNeeded(_env, usersExist: false, NullLogger.Instance);
        File.Exists(TokenPath).Should().BeTrue();

        AdminBootstrap.Consume(_env);

        File.Exists(TokenPath).Should().BeFalse();
    }

    [Fact]
    public void Consume_NoFile_DoesNotThrow()
    {
        var act = () => AdminBootstrap.Consume(_env);
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_AfterConsume_ReturnsFalse()
    {
        AdminBootstrap.EnsureBootstrapTokenIfNeeded(_env, usersExist: false, NullLogger.Instance);
        var token = File.ReadAllText(TokenPath).Trim();

        AdminBootstrap.Consume(_env);

        AdminBootstrap.Validate(_env, token).Should().BeFalse();
    }

    private static IConfiguration CfgWithTokenPath(string? path)
    {
        var dict = new Dictionary<string, string?>();
        if (path is not null) dict["Security:AdminSetupTokenPath"] = path;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void EnsureBootstrapTokenIfNeeded_OverridePathAbsolute_WritesToCustomLocation()
    {
        var customDir = Path.Combine(_tempDir, "data-" + Guid.NewGuid());
        Directory.CreateDirectory(customDir);
        var customPath = Path.Combine(customDir, "custom-setup.token");
        try
        {
            AdminBootstrap.EnsureBootstrapTokenIfNeeded(_env, usersExist: false, NullLogger.Instance,
                CfgWithTokenPath(customPath));

            File.Exists(customPath).Should().BeTrue();
            File.Exists(TokenPath).Should().BeFalse(
                "the override should steer the generator away from ContentRoot");
        }
        finally { try { Directory.Delete(customDir, true); } catch { } }
    }

    [Fact]
    public void Validate_UsesOverridePath()
    {
        var customDir = Path.Combine(_tempDir, "data-" + Guid.NewGuid());
        Directory.CreateDirectory(customDir);
        var customPath = Path.Combine(customDir, "custom-setup.token");
        try
        {
            var cfg = CfgWithTokenPath(customPath);
            AdminBootstrap.EnsureBootstrapTokenIfNeeded(_env, usersExist: false, NullLogger.Instance, cfg);
            var token = File.ReadAllText(customPath).Trim();

            AdminBootstrap.Validate(_env, token, cfg).Should().BeTrue();
            AdminBootstrap.Validate(_env, "wrong", cfg).Should().BeFalse();

            // The default (ContentRoot) path is NOT consulted when an override is set.
            AdminBootstrap.Validate(_env, token).Should().BeFalse();
        }
        finally { try { Directory.Delete(customDir, true); } catch { } }
    }

    [Fact]
    public void Consume_RemovesOverridePath()
    {
        var customDir = Path.Combine(_tempDir, "data-" + Guid.NewGuid());
        Directory.CreateDirectory(customDir);
        var customPath = Path.Combine(customDir, "custom-setup.token");
        try
        {
            var cfg = CfgWithTokenPath(customPath);
            AdminBootstrap.EnsureBootstrapTokenIfNeeded(_env, usersExist: false, NullLogger.Instance, cfg);
            File.Exists(customPath).Should().BeTrue();

            AdminBootstrap.Consume(_env, cfg);

            File.Exists(customPath).Should().BeFalse();
        }
        finally { try { Directory.Delete(customDir, true); } catch { } }
    }

    [Fact]
    public void EnsureBootstrapTokenIfNeeded_OverridePathRelative_ResolvesAgainstContentRoot()
    {
        AdminBootstrap.EnsureBootstrapTokenIfNeeded(_env, usersExist: false, NullLogger.Instance,
            CfgWithTokenPath("nested/setup.token"));

        File.Exists(Path.Combine(_tempDir, "nested", "setup.token")).Should().BeTrue();
    }

    [Fact]
    public void EnsureBootstrapTokenIfNeeded_ExistingBroadAcl_FailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;

        AdminBootstrap.EnsureBootstrapTokenIfNeeded(
            _env, usersExist: false, NullLogger.Instance);
        GrantEveryoneRead(TokenPath);

        Action act = () => AdminBootstrap.EnsureBootstrapTokenIfNeeded(
            _env, usersExist: false, NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*bootstrap-token security validation failed*");
    }

    [Fact]
    public void Validate_ExistingBroadAcl_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows()) return;

        AdminBootstrap.EnsureBootstrapTokenIfNeeded(
            _env, usersExist: false, NullLogger.Instance);
        var token = File.ReadAllText(TokenPath).Trim();
        GrantEveryoneRead(TokenPath);

        AdminBootstrap.Validate(_env, token).Should().BeFalse();
    }

    [Fact]
    public void EnsureBootstrapTokenIfNeeded_ExistingReparsePoint_FailsClosed()
    {
        var target = Path.Combine(_tempDir, "attacker-token.txt");
        File.WriteAllText(target, Convert.ToBase64String(new byte[32]));
        try
        {
            File.CreateSymbolicLink(TokenPath, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                   or IOException
                                   or PlatformNotSupportedException)
        {
            // Windows requires symlink privilege unless Developer Mode is enabled.
            return;
        }

        Action act = () => AdminBootstrap.EnsureBootstrapTokenIfNeeded(
            _env, usersExist: false, NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*bootstrap-token security validation failed*");
    }

    private static void GrantEveryoneRead(string path)
    {
        var info = new FileInfo(path);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read,
            AccessControlType.Allow));
        info.SetAccessControl(acl);
    }
}
