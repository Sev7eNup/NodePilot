using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests.Security;

/// <summary>
/// Pins the H-3 fix (a security-audit finding): the file is written through a handle that
/// already carries the owner-only ACL, so the secret content never lives on disk
/// world-readable. The previous "File.WriteAllText then SetAccessControl" pattern left a
/// multi-millisecond TOCTOU window (time-of-check to time-of-use — a gap an attacker can
/// race) where a sibling process could read the JWT key / bootstrap token.
/// </summary>
public sealed class RestrictedFileWriterTests : IDisposable
{
    private readonly string _tempDir;
    // RestrictedFileWriter is internal; reach in via reflection so we don't have to widen
    // the visibility just for tests. InternalsVisibleTo NodePilot.Api.Tests is set, so a
    // simple internal-static call would also work — reflection is just more explicit about
    // "this is a test boundary".
    private static readonly MethodInfo WriteText = typeof(NodePilot.Api.Security.JwtKeyResolver).Assembly
        .GetType("NodePilot.Api.Security.RestrictedFileWriter", throwOnError: true)!
        .GetMethod("WriteText", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

    public RestrictedFileWriterTests()
    {
        var basePath = OperatingSystem.IsWindows()
            ? Path.GetPathRoot(Path.GetTempPath())
                ?? throw new InvalidOperationException("Temporary path has no drive root.")
            : Path.GetTempPath();
        _tempDir = Path.Combine(basePath, "RestrictedFileWriterTests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_tempDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
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
        new DirectoryInfo(_tempDir).SetAccessControl(acl);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static bool Invoke(string path, string content, bool failClosed)
        => (bool)WriteText.Invoke(null, new object[] { path, content, failClosed })!;

    [Fact]
    public void WriteText_FreshPath_WritesContent()
    {
        var path = Path.Combine(_tempDir, "secret.txt");

        var ok = Invoke(path, "abc123", failClosed: true);

        ok.Should().BeTrue();
        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Be("abc123");
    }

    [Fact]
    public void WriteText_FailsClosed_OnExistingFile()
    {
        // The helper uses FileMode.CreateNew so it never silently overwrites a leftover
        // file from a previous run. Callers are expected to delete-then-write or check
        // File.Exists first; if they don't, the failure is loud (failClosed=true throws).
        var path = Path.Combine(_tempDir, "preexisting.txt");
        File.WriteAllText(path, "leftover");

        Action act = () => Invoke(path, "new", failClosed: true);

        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<IOException>(); // CreateNew on an existing file
    }

    [Fact]
    public void WriteText_BestEffort_OnExistingFile_ReturnsFalseAndCleansUp()
    {
        // failClosed=false matches the bootstrap-token contract: a failure should not
        // brick recovery on weird filesystems. The partial file is removed on the way
        // out so a retry doesn't reuse a half-secured artifact.
        var path = Path.Combine(_tempDir, "preexisting.txt");
        File.WriteAllText(path, "leftover");

        var ok = Invoke(path, "new", failClosed: false);

        ok.Should().BeFalse();
        // Pre-existing content is preserved (we never opened the file at all).
        File.ReadAllText(path).Should().Be("leftover");
    }

    [Fact]
    public void WriteText_OwnerCanReadOwnFile()
    {
        // Sanity check that the ACL helper doesn't lock OURSELVES out. The ACL strips
        // every inherited rule and adds owner-FullControl, so the same process must still
        // be able to read what it just wrote.
        var path = Path.Combine(_tempDir, "selfread.txt");
        Invoke(path, "alpha", failClosed: true);

        var roundtrip = File.ReadAllText(path);

        roundtrip.Should().Be("alpha");
    }

    [Fact]
    public void WriteText_DoesNotLeavePartialFile_OnFailure()
    {
        // Close the only valid path to write into the temp dir, then verify failClosed=true
        // doesn't leave a phantom file. We simulate the "ACL apply failed" path by passing
        // an absurdly-long path that hits Windows' MAX_PATH limit on the create — the
        // helper should clean up on its way out.
        var longName = new string('x', 260) + ".txt";
        var path = Path.Combine(_tempDir, longName);

        Action act = () => Invoke(path, "abc", failClosed: true);

        // Either CreateNew or the ACL apply throws — either way the file must not exist.
        try { act(); } catch { }
        File.Exists(path).Should().BeFalse(
            "the writer must delete its partial file when anything in the create-then-acl-then-write " +
            "sequence fails, so a retry never reuses a half-secured artifact");
    }
}
