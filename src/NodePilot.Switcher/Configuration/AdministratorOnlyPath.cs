using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NodePilot.Switcher.Configuration;

/// <summary>
/// Decides whether a file or folder that the elevated switcher reads or runs can be changed only
/// by administrators. Anything else would let a standard user decide what runs as administrator.
/// </summary>
internal static class AdministratorOnlyPath
{
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    // Rights that change the content, delete or rename the object or its children, or rewrite its ACL.
    private const int MutatingRights =
        (int)(FileSystemRights.Write
              | FileSystemRights.Delete
              | FileSystemRights.DeleteSubdirectoriesAndFiles
              | FileSystemRights.ChangePermissions
              | FileSystemRights.TakeOwnership)
        | GenericAll
        | GenericWrite;

    // TrustedInstaller holds inherited full control on every folder below Program Files.
    private static readonly HashSet<SecurityIdentifier> Trusted =
    [
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.LocalSystemSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"),
    ];

    /// <summary>
    /// Returns why <paramref name="security"/> lets a non-administrator change the object, or null
    /// when only Administrators, SYSTEM and TrustedInstaller own or may change it.
    /// </summary>
    internal static string? FindViolation(FileSystemSecurity security)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
            return "has no readable owner";
        if (!Trusted.Contains(owner))
            return $"is owned by {Describe(owner)}";

        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            // Inherit-only entries apply to children; the children that matter are checked themselves.
            if (rule.AccessControlType != AccessControlType.Allow
                || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
                || ((int)rule.FileSystemRights & MutatingRights) == 0)
                continue;

            if (rule.IdentityReference is not SecurityIdentifier sid)
                return "has an access entry whose account cannot be read";
            if (!Trusted.Contains(sid))
                return $"lets {Describe(sid)} change, delete or re-permission it";
        }

        return null;
    }

    /// <summary>
    /// Reads the owner and ACL of a file or folder and returns why it is not administrator-only,
    /// or null. Anything that cannot be verified counts as a violation.
    /// </summary>
    internal static string? FindViolation(string path)
    {
        // The SIDs in a remote security descriptor name the remote machine's groups, not ours.
        if (!IsOnLocalFixedDrive(path))
            return $"'{path}' is not on a local drive, so its permissions cannot be verified";

        try
        {
            const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Access;
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(sections)
                : new FileInfo(path).GetAccessControl(sections);
            var reason = FindViolation(security);
            return reason is null ? null : $"'{path}' {reason}";
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidOperationException
                                              or ArgumentException)
        {
            return $"the permissions of '{path}' could not be read: {exception.Message}";
        }
    }

    private static bool IsOnLocalFixedDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (ArgumentException)
        {
            // UNC and device paths have no drive letter.
            return false;
        }
    }

    private static string Describe(SecurityIdentifier sid)
    {
        try
        {
            return $"{sid.Translate(typeof(NTAccount)).Value} ({sid.Value})";
        }
        catch (SystemException)
        {
            // A deleted account still holds its entry; the raw SID is what removes it.
            return sid.Value;
        }
    }
}
