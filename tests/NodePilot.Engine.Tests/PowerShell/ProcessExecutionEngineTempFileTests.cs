using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

public class ProcessExecutionEngineTempFileTests
{
    [Fact]
    public async Task WritePrivateScriptAsync_CreatesOwnerOnlyFile()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = Path.Combine(Path.GetTempPath(), $"nodepilot_acl_test_{Guid.NewGuid():N}.ps1");
        try
        {
            var method = typeof(ProcessExecutionEngine).GetMethod(
                "WritePrivateScriptAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            method.Should().NotBeNull();

            await (Task)method!.Invoke(null, [path, "Write-Output 'secret'", CancellationToken.None])!;

            var owner = WindowsIdentity.GetCurrent().User;
            var rules = new FileInfo(path)
                .GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToList();

            rules.Should().ContainSingle();
            rules[0].IdentityReference.Should().Be(owner);
            rules[0].FileSystemRights.Should().HaveFlag(FileSystemRights.FullControl);
            rules[0].AccessControlType.Should().Be(AccessControlType.Allow);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
