using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using NodePilot.Switcher.Configuration;
using NodePilot.Switcher.Services;
using Xunit;

namespace NodePilot.Switcher.Tests;

public sealed class AdministratorOnlyPathTests : IDisposable
{
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"switcher-acl-{Guid.NewGuid():N}");

    public AdministratorOnlyPathTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void FindViolation_AdministratorOwnedWithReadOnlyUsers_ReturnsNull()
    {
        var security = FileSecurity(Administrators,
            (Administrators, FileSystemRights.FullControl),
            (LocalSystem, FileSystemRights.FullControl),
            (Users, FileSystemRights.ReadAndExecute));

        AdministratorOnlyPath.FindViolation(security).Should().BeNull();
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData(TrustedInstallerSid)]
    public void FindViolation_OwnedBySystemOrTrustedInstaller_ReturnsNull(string ownerSid)
    {
        var security = FileSecurity(new SecurityIdentifier(ownerSid), (Administrators, FileSystemRights.FullControl));

        AdministratorOnlyPath.FindViolation(security).Should().BeNull();
    }

    // The owner can always rewrite the ACL, so a standard user who created the file keeps control.
    [Fact]
    public void FindViolation_OwnedByAStandardAccount_ReportsTheOwner()
    {
        var security = FileSecurity(Users, (Administrators, FileSystemRights.FullControl));

        AdministratorOnlyPath.FindViolation(security).Should().Contain("is owned by").And.Contain(Users.Value);
    }

    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.WriteAttributes)]
    [InlineData(FileSystemRights.WriteExtendedAttributes)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData(FileSystemRights.Modify)]
    [InlineData(FileSystemRights.FullControl)]
    public void FindViolation_StandardAccountWithMutatingRight_ReportsTheAccount(FileSystemRights rights)
    {
        var security = FileSecurity(Administrators,
            (Administrators, FileSystemRights.FullControl),
            (Users, rights));

        AdministratorOnlyPath.FindViolation(security).Should().Contain(Users.Value);
    }

    [Fact]
    public void FindViolation_InheritedWriteEntry_ReportsTheAccount()
    {
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm("O:BAD:(A;;FA;;;BA)(A;ID;0x1301bf;;;BU)");
        security.GetAccessRules(includeExplicit: false, includeInherited: true, typeof(SecurityIdentifier))
            .Count.Should().Be(1);

        AdministratorOnlyPath.FindViolation(security).Should().Contain(Users.Value);
    }

    [Theory]
    [InlineData("GW")]
    [InlineData("GA")]
    public void FindViolation_GenericWriteOrAllEntry_ReportsTheAccount(string genericRight)
    {
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm($"O:BAD:P(A;;FA;;;BA)(A;;{genericRight};;;BU)");

        AdministratorOnlyPath.FindViolation(security).Should().Contain(Users.Value);
    }

    // Inherit-only entries only shape future children, which are checked on their own.
    [Fact]
    public void FindViolation_InheritOnlyWriteEntryOnAFolder_IsIgnored()
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.AddAccessRule(new FileSystemAccessRule(
            Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            Users,
            FileSystemRights.Modify,
            InheritanceFlags.ObjectInherit,
            PropagationFlags.InheritOnly,
            AccessControlType.Allow));

        AdministratorOnlyPath.FindViolation(security).Should().BeNull();
    }

    // The ACL a folder below Program Files inherits, as installers leave it for tools\np.
    [Fact]
    public void FindViolation_FolderBelowProgramFiles_ReturnsNull()
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(
            $"O:BAD:AI(A;ID;FA;;;{TrustedInstallerSid})(A;CIIOID;GA;;;{TrustedInstallerSid})"
            + "(A;ID;FA;;;SY)(A;OICIIOID;GA;;;SY)(A;ID;FA;;;BA)(A;OICIIOID;GA;;;BA)"
            + "(A;ID;0x1200a9;;;BU)(A;OICIIOID;GXGR;;;BU)(A;OICIIOID;GA;;;CO)"
            + "(A;ID;0x1200a9;;;AC)(A;OICIIOID;GXGR;;;AC)");

        AdministratorOnlyPath.FindViolation(security).Should().BeNull();
    }

    [Fact]
    public void FindViolation_FolderProtectedByTheActivityLogger_ReturnsNull()
    {
        var security = ActivityLogger.CreateProtectedDirectorySecurity();

        security.GetOwner(typeof(SecurityIdentifier)).Should().Be(Administrators);
        security.AreAccessRulesProtected.Should().BeTrue();
        AdministratorOnlyPath.FindViolation(security).Should().BeNull();
    }

    [Fact]
    public void FindViolation_FileWritableByEveryone_ReportsThePath()
    {
        var path = Path.Combine(_directory, "np.exe");
        File.WriteAllText(path, string.Empty);
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Allow));
        info.SetAccessControl(security);

        AdministratorOnlyPath.FindViolation(path).Should().Contain(path);
    }

    [Fact]
    public void FindViolation_MissingPath_FailsClosed()
    {
        var path = Path.Combine(_directory, "absent.json");

        AdministratorOnlyPath.FindViolation(path).Should().Contain("could not be read").And.Contain(path);
    }

    // A remote descriptor names the server's Administrators group, not this machine's.
    [Fact]
    public void FindViolation_UncPath_FailsClosed()
    {
        AdministratorOnlyPath.FindViolation(@"\\server\share\np.exe")
            .Should().Contain("not on a local drive");
    }

    private static FileSecurity FileSecurity(
        SecurityIdentifier owner,
        params (SecurityIdentifier Sid, FileSystemRights Rights)[] allow)
    {
        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var (sid, rights) in allow)
            security.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
        return security;
    }
}
