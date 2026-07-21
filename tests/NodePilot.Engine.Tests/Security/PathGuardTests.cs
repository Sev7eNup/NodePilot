using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Engine.Security;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Security;

public class PathGuardTests
{
    private static IConfiguration Cfg(params (string Key, string Value)[] kv)
    {
        var dict = kv.ToDictionary(p => p.Key, p => (string?)p.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Theory]
    [InlineData("C:\\temp\\..\\windows\\system32")]
    [InlineData("..")]
    [InlineData("../../etc/passwd")]
    [InlineData("C:\\foo\\..")]
    public void Default_RejectsTraversal(string path)
    {
        // Secure-by-default: a missing FileSystemOperation:RejectTraversal key now reads as
        // "true" so a stripped-down appsettings falls on the safe side. Legacy admin scripts
        // that genuinely need relative paths must opt out via RejectTraversal=false.
        Action act = () => PathGuard.Validate(Cfg(), path);
        act.Should().Throw<InvalidOperationException>().WithMessage("*traversal*");
    }

    [Theory]
    [InlineData("C:\\temp\\..\\windows\\system32")]
    [InlineData("..")]
    [InlineData("../../etc/passwd")]
    [InlineData("C:\\foo\\..")]
    public void WhenExplicitlyDisabled_AllowsTraversal(string path)
    {
        // Dev-mode escape hatch (mirrors appsettings.Development.json). Once an operator
        // sets RejectTraversal=false, traversal flows through untouched.
        Action act = () => PathGuard.Validate(
            Cfg(("FileSystemOperation:RejectTraversal", "false")),
            path);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("C:\\temp\\..\\windows\\system32")]
    [InlineData("../../etc/passwd")]
    public void WhenExplicitlyEnabled_RejectsTraversal(string path)
    {
        Action act = () => PathGuard.Validate(
            Cfg(("FileSystemOperation:RejectTraversal", "true")),
            path);
        act.Should().Throw<InvalidOperationException>().WithMessage("*traversal*");
    }

    [Theory]
    [InlineData("\\\\attacker.com\\share\\payload.exe")]
    [InlineData("\\\\10.0.0.5\\admin$\\hosts")]
    [InlineData("//attacker.com/share/payload")]
    [InlineData("\\\\?\\UNC\\attacker\\c$\\Windows")]
    [InlineData("\\\\.\\PIPE\\nodepilot")]
    public void UncPath_AlwaysRejected_RegardlessOfFlag(string path)
    {
        // UNC-block is unconditional: the rule fires even with RejectTraversal=false because
        // the attack surface (NTLM relay against an attacker-controlled SMB host) is real
        // regardless of the traversal posture. Forward-slash and extended-namespace forms
        // cover variants that a naive backslash-only check would miss.
        Action defaultCfg = () => PathGuard.Validate(Cfg(), path);
        defaultCfg.Should().Throw<InvalidOperationException>().WithMessage("*UNC*");

        Action permissiveCfg = () => PathGuard.Validate(
            Cfg(("FileSystemOperation:RejectTraversal", "false")),
            path);
        permissiveCfg.Should().Throw<InvalidOperationException>().WithMessage("*UNC*");
    }

    [Fact]
    public void PlainAbsolutePath_Accepted()
    {
        Action act = () => PathGuard.Validate(Cfg(), "C:\\temp\\file.txt");
        act.Should().NotThrow();
    }

    [Fact]
    public void EmptyPath_Rejected()
    {
        Action act = () => PathGuard.Validate(Cfg(), "");
        act.Should().Throw<InvalidOperationException>().WithMessage("*empty*");
    }

    [Theory]
    [InlineData("C:\\data\\*.txt")]
    [InlineData("C:\\data\\file?.txt")]
    public void WildcardPath_RejectedByDefault(string path)
    {
        Action act = () => PathGuard.Validate(Cfg(), path);
        act.Should().Throw<InvalidOperationException>().WithMessage("*wildcard*");
    }

    [Fact]
    public void WildcardPath_AllowedWhenGlobCapableActivityOptsIn()
    {
        Action act = () => PathGuard.Validate(Cfg(), "C:\\data\\*.txt", allowWildcards: true);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("new.txt")]
    [InlineData("folder-name")]
    public void LeafName_PlainName_Accepted(string name)
    {
        Action act = () => PathGuard.ValidateLeafName(name);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\secret.txt")]
    [InlineData("../secret.txt")]
    [InlineData("C:\\secret.txt")]
    [InlineData("bad:name.txt")]
    [InlineData("CON")]
    [InlineData("name.")]
    public void LeafName_PathLikeOrReservedName_Rejected(string name)
    {
        Action act = () => PathGuard.ValidateLeafName(name);
        act.Should().Throw<InvalidOperationException>().WithMessage("*newName*");
    }

    [Fact]
    public void AllowedRoots_PathInsideRoot_Accepted()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FileSystemOperation:AllowedRoots:0"] = "C:\\data",
        }).Build();

        Action act = () => PathGuard.Validate(cfg, "C:\\data\\subdir\\file.txt");
        act.Should().NotThrow();
    }

    [Fact]
    public void AllowedRoots_PathOutsideRoot_Rejected()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FileSystemOperation:AllowedRoots:0"] = "C:\\data",
        }).Build();

        Action act = () => PathGuard.Validate(cfg, "C:\\windows\\system32\\config\\SAM");
        act.Should().Throw<InvalidOperationException>().WithMessage("*AllowedRoots*");
    }

    [WindowsFact]
    public void AllowedRoots_ResolvesDirectorySymlinkBeforeRootComparison()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "nodepilot-pathguard-" + Guid.NewGuid().ToString("N"));
        var allowed = Path.Combine(baseDir, "allowed");
        var outside = Path.Combine(baseDir, "outside");
        var link = Path.Combine(allowed, "link");

        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(outside);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileSystemOperation:AllowedRoots:0"] = allowed,
            }).Build();

            Action act = () => PathGuard.Validate(cfg, Path.Combine(link, "file.txt"));
            act.Should().Throw<InvalidOperationException>().WithMessage("*AllowedRoots*");
        }
        finally
        {
            if (Directory.Exists(baseDir))
                Directory.Delete(baseDir, recursive: true);
        }
    }
}
