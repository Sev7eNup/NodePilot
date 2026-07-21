using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace NodePilot.Api.Security;

/// <summary>
/// Writes a secret file with owner-only permissions applied **before** the secret content
/// hits disk. Closes a TOCTOU (time-of-check-to-time-of-use) race — security-audit finding
/// H-3 — where the previous "write content first, then call SetAccessControl" pattern left
/// the secret world-readable for the few milliseconds between File.WriteAllText returning
/// and the ACL helper finishing.
///
/// On Windows: create the file with a restrictive NTFS security descriptor (no inheritance,
/// owner FullControl only) in the same operation that creates the file. On POSIX: create it
/// with mode 0600. No inherited-permission window is externally observable.
///
/// On any failure, the freshly-created file is deleted so a retry cannot reuse a partially-
/// secured artifact. The <c>failClosed</c> parameter on <see cref="WriteText"/> selects
/// between hard-fail (rethrow) for long-lived secrets like the JWT signing key, and
/// best-effort (return false) when a Development-only caller explicitly accepts that tradeoff.
/// </summary>
internal static class RestrictedFileWriter
{
    private static readonly SecurityIdentifier LocalSystemSid =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstallerSid =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly SecurityIdentifier CreatorOwnerSid = new("S-1-3-0");
    private static readonly SecurityIdentifier OwnerRightsSid = new("S-1-3-4");

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> as UTF-8, with owner-only
    /// ACLs applied before the content is written. When <paramref name="failClosed"/> is true
    /// any ACL failure rethrows after the partial file is deleted; when false the caller gets
    /// a false return value and must decide how to recover.
    /// </summary>
    public static bool WriteText(string path, string content, bool failClosed)
    {
        // Bind validation and creation to one absolute name. Re-evaluating a relative path
        // after validation would let another thread change CurrentDirectory in between.
        path = Path.GetFullPath(path);
        FileStream? stream = null;
        var createdFile = false;
        try
        {
            var parentSecurity = ValidateParentDirectory(path);
            if (!parentSecurity.IsSecure)
            {
                throw new InvalidOperationException(
                    $"Secret file parent directory is insecure: {parentSecurity.Reason}");
            }

            // FileMode.CreateNew → fail if path already exists. The two callers (JWT key,
            // bootstrap token) both check File.Exists first; using CreateNew here turns a
            // race in the caller into a hard error instead of silently overwriting. If
            // CreateNew throws, we have NOT touched the filesystem — never delete somebody
            // else's pre-existing artifact in the catch block.
            stream = OperatingSystem.IsWindows()
                ? CreateWindowsFileWithRestrictiveAcl(path)
                : new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 4096,
                    Options = FileOptions.WriteThrough,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
            createdFile = true;

            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception)
        {
            // Whatever went wrong — ACL refused, write failed, antivirus quarantined — leave
            // no partial file behind. We only delete files we actually created (createdFile
            // is true), so a CreateNew failure on a pre-existing path leaves it untouched.
            try { stream?.Dispose(); } catch { /* best-effort: stream may already be disposed */ }
            stream = null;
            if (createdFile)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort: file may be locked */ }
            }

            if (failClosed) throw;
            return false;
        }
        finally
        {
            try { stream?.Dispose(); } catch { /* best-effort: stream may already be disposed in catch */ }
        }
    }

    /// <summary>
    /// Validates a pre-existing secret before a caller reads it. A key on a filesystem that
    /// cannot persist ACLs, behind a reparse point, or readable by an untrusted principal is
    /// equivalent to a disclosed key and must not be used by a production process.
    /// </summary>
    public static ExistingSecretFileSecurity ValidateExisting(string path)
        => InspectExisting(path, readContent: false).Security;

    /// <summary>
    /// Validates and reads a pre-existing secret while one non-delete-sharing handle remains
    /// open for the entire operation. This binds the ACL decision to the bytes returned to the
    /// caller instead of validating a pathname and reopening it after an attacker-controlled
    /// replacement.
    /// </summary>
    public static ExistingSecretFileRead ReadValidatedText(string path)
        => InspectExisting(path, readContent: true);

    /// <summary>
    /// Validates every lexical parent of a secret path. The immediate directory must not be
    /// writable by an untrusted principal, and no ancestor may be replaceable or be a reparse
    /// point. Read-only access for ordinary users is harmless; mutation/delete rights are not.
    /// </summary>
    public static ExistingSecretFileSecurity ValidateParentDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parentPath = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parentPath))
            return ExistingSecretFileSecurity.Invalid("the file has no verifiable parent directory");

        if (OperatingSystem.IsWindows() && !IsWindowsAclCapableFileSystem(fullPath))
            return ExistingSecretFileSecurity.Invalid(
                "the filesystem does not provide persistent Windows ACLs");

        var immediate = true;
        var directory = new DirectoryInfo(parentPath);
        while (directory is not null)
        {
            try
            {
                directory.Refresh();
                if (!directory.Exists)
                    return ExistingSecretFileSecurity.Invalid(
                        $"parent directory '{directory.FullName}' does not exist");
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    return ExistingSecretFileSecurity.Invalid(
                        $"parent directory '{directory.FullName}' is a reparse point");

                if (OperatingSystem.IsWindows())
                {
                    var security = ValidateWindowsDirectoryAcl(directory, immediate);
                    if (!security.IsSecure) return security;
                }
                else
                {
                    var mode = File.GetUnixFileMode(directory.FullName);
                    const UnixFileMode untrustedWrite =
                        UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
                    if ((mode & untrustedWrite) != 0)
                        return ExistingSecretFileSecurity.Invalid(
                            $"parent directory '{directory.FullName}' is writable by group or other");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or PlatformNotSupportedException or SystemException)
            {
                return ExistingSecretFileSecurity.Invalid(
                    $"parent directory '{directory.FullName}' permissions could not be verified");
            }

            immediate = false;
            directory = directory.Parent;
        }

        return ExistingSecretFileSecurity.Valid();
    }

    private static ExistingSecretFileRead InspectExisting(string path, bool readContent)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            return ExistingSecretFileRead.Invalid("the file does not exist");

        // FileShare.Read deliberately omits Write and Delete. On Windows, an existing writer
        // or delete-capable handle makes this open fail, and no replacement can occur until
        // validation and the optional read have both completed.
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            var parentSecurity = ValidateParentDirectory(fullPath);
            if (!parentSecurity.IsSecure)
                return new ExistingSecretFileRead(parentSecurity, ReadContentIfRequested(stream, readContent));

            info.Refresh();
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return ExistingSecretFileRead.Invalid(
                    "the file is a reparse point", ReadContentIfRequested(stream, readContent));

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(fullPath);
                const UnixFileMode groupOrOther =
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                var security = (mode & groupOrOther) == 0
                    ? ExistingSecretFileSecurity.Valid()
                    : ExistingSecretFileSecurity.Invalid(
                        "the file grants group or other permissions", canRotateSecurely: true);
                return new ExistingSecretFileRead(
                    security, ReadContentIfRequested(stream, readContent));
            }

            var acl = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null || !BuildTrustedSids().Contains(owner))
                return ExistingSecretFileRead.Invalid(
                    "the file owner is not a trusted service or operating-system identity",
                    ReadContentIfRequested(stream, readContent),
                    canRotateSecurely: true);

            if (!acl.AreAccessRulesProtected)
                return ExistingSecretFileRead.Invalid(
                    "ACL inheritance is enabled",
                    ReadContentIfRequested(stream, readContent),
                    canRotateSecurely: true);

            var rules = acl.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.IsInherited)
                    return ExistingSecretFileRead.Invalid(
                        "the file contains inherited access rules",
                        ReadContentIfRequested(stream, readContent),
                        canRotateSecurely: true);

                if (rule.AccessControlType == AccessControlType.Allow
                    && rule.IdentityReference is SecurityIdentifier sid
                    && !BuildTrustedSids().Contains(sid))
                {
                    return ExistingSecretFileRead.Invalid(
                        "the file grants access to an untrusted principal",
                        ReadContentIfRequested(stream, readContent),
                        canRotateSecurely: true);
                }
            }

            return new ExistingSecretFileRead(
                ExistingSecretFileSecurity.Valid(),
                ReadContentIfRequested(stream, readContent));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidOperationException or PlatformNotSupportedException
                                   or SystemException)
        {
            return ExistingSecretFileRead.Invalid(
                "the file, its owner, or its ACL could not be verified");
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static FileStream CreateWindowsFileWithRestrictiveAcl(string path)
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "Could not determine current Windows identity for ACL owner.");

        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        // The security descriptor is supplied to CreateFile itself. Creating first and calling
        // SetAccessControl later would expose a short inherited-ACL race.
        return System.IO.FileSystemAclExtensions.Create(
            new FileInfo(path),
            FileMode.CreateNew,
            FileSystemRights.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough,
            security);
    }

    private static string? ReadContentIfRequested(FileStream stream, bool readContent)
    {
        if (!readContent) return null;
        stream.Position = 0;
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static ExistingSecretFileSecurity ValidateWindowsDirectoryAcl(
        DirectoryInfo directory,
        bool immediate)
    {
        var trustedSids = BuildTrustedSids();
        var acl = directory.GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        var owner = acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !trustedSids.Contains(owner))
            return ExistingSecretFileSecurity.Invalid(
                $"parent directory '{directory.FullName}' has an untrusted owner");

        var dangerous = FileSystemRights.Delete
                        | FileSystemRights.DeleteSubdirectoriesAndFiles
                        | FileSystemRights.ChangePermissions
                        | FileSystemRights.TakeOwnership;
        if (immediate)
        {
            dangerous |= FileSystemRights.CreateFiles
                         | FileSystemRights.CreateDirectories
                         | FileSystemRights.WriteAttributes
                         | FileSystemRights.WriteExtendedAttributes;
        }

        var rules = acl.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow
                || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
                || rule.IdentityReference is not SecurityIdentifier sid
                || trustedSids.Contains(sid))
            {
                continue;
            }

            if ((rule.FileSystemRights & dangerous) != 0)
                return ExistingSecretFileSecurity.Invalid(
                    $"parent directory '{directory.FullName}' grants mutation rights to an untrusted principal");
        }

        return ExistingSecretFileSecurity.Valid();
    }

    private static HashSet<SecurityIdentifier> BuildTrustedSids()
    {
        var currentSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "Could not determine the current Windows identity.");
        return
        [
            currentSid,
            LocalSystemSid,
            AdministratorsSid,
            TrustedInstallerSid,
            CreatorOwnerSid,
            OwnerRightsSid,
        ];
    }

    private static bool IsWindowsAclCapableFileSystem(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root)) return false;
            var format = new DriveInfo(root).DriveFormat;
            return string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(format, "ReFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or SystemException)
        {
            // Unknown is unsafe here: production startup must not assume that a remote or
            // unusual filesystem enforces an ACL it could not inspect.
            return false;
        }
    }

}

internal sealed record ExistingSecretFileSecurity(
    bool IsSecure,
    string? Reason,
    bool CanRotateSecurely)
{
    public static ExistingSecretFileSecurity Valid() => new(true, null, false);
    public static ExistingSecretFileSecurity Invalid(string reason, bool canRotateSecurely = false)
        => new(false, reason, canRotateSecurely);
}

internal sealed record ExistingSecretFileRead(
    ExistingSecretFileSecurity Security,
    string? Content)
{
    public static ExistingSecretFileRead Invalid(
        string reason,
        string? content = null,
        bool canRotateSecurely = false)
        => new(ExistingSecretFileSecurity.Invalid(reason, canRotateSecurely), content);
}
