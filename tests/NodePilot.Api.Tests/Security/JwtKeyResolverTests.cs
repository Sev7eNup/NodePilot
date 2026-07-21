using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using NodePilot.Api.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public class JwtKeyResolverTests
{
    private static IConfiguration Cfg(string? key)
    {
        var dict = new Dictionary<string, string?>();
        if (key is not null) dict["Jwt:Key"] = key;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static IHostEnvironment Env(
        string contentRoot,
        string environmentName = "Development")
    {
        var mock = new Mock<IHostEnvironment>();
        mock.SetupGet(e => e.ContentRootPath).Returns(contentRoot);
        mock.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        return mock.Object;
    }

    private static string CreateSecureTestRoot()
    {
        var basePath = OperatingSystem.IsWindows()
            ? Path.GetPathRoot(Path.GetTempPath())
                ?? throw new InvalidOperationException("Temporary path has no drive root.")
            : Path.GetTempPath();
        var root = Path.Combine(basePath, "NodePilotJwtAclTests-" + Guid.NewGuid());
        Directory.CreateDirectory(root);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return root;
        }

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
        new DirectoryInfo(root).SetAccessControl(acl);
        return root;
    }

    [Fact]
    public void Validate_Empty_Throws()
    {
        Action act = () => JwtKeyResolver.Validate("");
        act.Should().Throw<InvalidOperationException>().WithMessage("*empty*");
    }

    [Fact]
    public void Validate_BannedDefault_Throws()
    {
        // The legacy default shipped in appsettings.json before 2026-04-18.
        Action act = () => JwtKeyResolver.Validate("NodePilot-Default-Secret-Key-Change-In-Production-32chars!");
        act.Should().Throw<InvalidOperationException>().WithMessage("*default*");
    }

    [Fact]
    public void Validate_TooShort_Throws()
    {
        Action act = () => JwtKeyResolver.Validate("short-key");
        act.Should().Throw<InvalidOperationException>().WithMessage("*32 bytes*");
    }

    [Fact]
    public void Validate_RandomLongKey_Accepts()
    {
        var key = new string('k', 48);
        Action act = () => JwtKeyResolver.Validate(key);
        act.Should().NotThrow();
    }

    [Fact]
    public void Resolve_ConfigKey_PreferredOverFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "JwtKeyResolverTests-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        try
        {
            // A different file key exists on disk but config wins.
            File.WriteAllText(Path.Combine(tmp, JwtKeyResolver.KeyFileName), new string('f', 48));

            var key = JwtKeyResolver.Resolve(Cfg(new string('c', 48)), Env(tmp));

            key.Should().Be(new string('c', 48));
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Resolve_NoConfig_GeneratesFileWithRandomKey()
    {
        var tmp = CreateSecureTestRoot();
        try
        {
            var key = JwtKeyResolver.Resolve(Cfg(null), Env(tmp));

            key.Should().NotBeNullOrEmpty();
            System.Text.Encoding.UTF8.GetByteCount(key).Should().BeGreaterThanOrEqualTo(JwtKeyResolver.MinKeyBytes);
            File.Exists(Path.Combine(tmp, JwtKeyResolver.KeyFileName)).Should().BeTrue();
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Resolve_BannedDefaultInConfig_ThrowsAtStartup()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "JwtKeyResolverTests-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        try
        {
            Action act = () => JwtKeyResolver.Resolve(
                Cfg("NodePilot-Default-Secret-Key-Change-In-Production-32chars!"),
                Env(tmp));

            act.Should().Throw<InvalidOperationException>();
        }
        finally { Directory.Delete(tmp, true); }
    }

    private static IConfiguration CfgWithKeyPath(string? keyPath)
    {
        var dict = new Dictionary<string, string?>();
        if (keyPath is not null) dict["Jwt:KeyPath"] = keyPath;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static IConfiguration CfgWithKeyPath(string keyPath, bool rotateInsecure)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:KeyPath"] = keyPath,
            ["Jwt:RotateInsecureKeyFile"] = rotateInsecure.ToString(),
        }).Build();

    [Fact]
    public void Resolve_JwtKeyPathAbsolute_WritesToConfiguredLocation()
    {
        var contentRoot = CreateSecureTestRoot();
        var dataDir = CreateSecureTestRoot();
        Directory.CreateDirectory(contentRoot);
        Directory.CreateDirectory(dataDir);
        var customPath = Path.Combine(dataDir, "custom-jwt.key");
        try
        {
            var key = JwtKeyResolver.Resolve(CfgWithKeyPath(customPath), Env(contentRoot));

            File.Exists(customPath).Should().BeTrue();
            File.Exists(Path.Combine(contentRoot, JwtKeyResolver.KeyFileName)).Should().BeFalse(
                "the override should steer the generator away from ContentRoot");
            System.Text.Encoding.UTF8.GetByteCount(key).Should().BeGreaterThanOrEqualTo(JwtKeyResolver.MinKeyBytes);
        }
        finally
        {
            try { Directory.Delete(contentRoot, true); } catch { }
            try { Directory.Delete(dataDir, true); } catch { }
        }
    }

    [Fact]
    public void Resolve_JwtKeyPathRelative_ResolvesAgainstContentRoot()
    {
        var contentRoot = CreateSecureTestRoot();
        try
        {
            JwtKeyResolver.Resolve(CfgWithKeyPath("subdir/jwt.key"), Env(contentRoot));

            File.Exists(Path.Combine(contentRoot, "subdir", "jwt.key")).Should().BeTrue();
        }
        finally { try { Directory.Delete(contentRoot, true); } catch { } }
    }

    [Fact]
    public void Resolve_JwtKeyPath_CreatesMissingDirectory()
    {
        var contentRoot = CreateSecureTestRoot();
        var dataRoot = CreateSecureTestRoot();
        var dataDir = Path.Combine(dataRoot, "nested", "deep");
        var customPath = Path.Combine(dataDir, "jwt.key");
        try
        {
            JwtKeyResolver.Resolve(CfgWithKeyPath(customPath), Env(contentRoot));

            File.Exists(customPath).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(contentRoot, true); } catch { }
            try
            {
                Directory.Delete(dataRoot, true);
            }
            catch { }
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Resolve_NonDevelopmentExistingKeyWithBroadAcl_FailsClosed(
        string environmentName)
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = CreateSecureTestRoot();
        var path = Path.Combine(root, "jwt.key");
        try
        {
            // Generate a known-good file first, then deliberately grant Everyone read access.
            JwtKeyResolver.Resolve(CfgWithKeyPath(path), Env(root));
            var info = new FileInfo(path);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read,
                AccessControlType.Allow));
            info.SetAccessControl(acl);

            Action act = () => JwtKeyResolver.Resolve(
                CfgWithKeyPath(path),
                Env(root, environmentName));

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*security validation failed*");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Resolve_ExplicitRotation_ReplacesInsecureExistingKeyAndSecuresFile()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = CreateSecureTestRoot();
        var path = Path.Combine(root, "jwt.key");
        var oldKey = new string('x', 48);
        try
        {
            File.WriteAllText(path, oldKey);

            var rotated = JwtKeyResolver.Resolve(
                CfgWithKeyPath(path, rotateInsecure: true),
                Env(root, Environments.Production));

            rotated.Should().NotBe(oldKey);
            JwtKeyResolver.Validate(rotated);

            var validateMethod = typeof(JwtKeyResolver).Assembly
                .GetType("NodePilot.Api.Security.RestrictedFileWriter", throwOnError: true)!
                .GetMethod("ValidateExisting", System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;
            var result = validateMethod.Invoke(null, new object[] { path });
            result!.GetType().GetProperty("IsSecure")!.GetValue(result).Should().Be(true);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Resolve_NonDevelopmentKeyUnderWritableParent_FailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = CreateSecureTestRoot();
        var path = Path.Combine(root, "jwt.key");
        try
        {
            JwtKeyResolver.Resolve(CfgWithKeyPath(path), Env(root));

            var acl = new DirectoryInfo(root).GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(root).SetAccessControl(acl);

            Action act = () => JwtKeyResolver.Resolve(
                CfgWithKeyPath(path),
                Env(root, Environments.Production));

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*parent directory*mutation rights*");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
