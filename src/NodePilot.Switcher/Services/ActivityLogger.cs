using System.Collections.Concurrent;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NodePilot.Switcher.Services;

internal sealed record ActivityEntry(DateTimeOffset Timestamp, string Level, string Message, string? ServiceName = null);

internal interface IActivityLogger
{
    event EventHandler<ActivityEntry>? EntryWritten;
    IReadOnlyList<ActivityEntry> Entries { get; }
    void Info(string message, string? serviceName = null);
    void Success(string message, string? serviceName = null);
    void Error(string message, string? serviceName = null);
}

internal sealed class ActivityLogger : IActivityLogger
{
    private const long MaxLogBytes = 1024 * 1024;
    private readonly ConcurrentQueue<ActivityEntry> _entries = new();
    private readonly object _fileGate = new();
    private readonly string? _logFile;

    public ActivityLogger(string? logDirectory = null)
    {
        try
        {
            var directory = logDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NodePilot",
                "Switcher");
            EnsureProtectedDirectory(directory);
            _logFile = Path.Combine(directory, "switcher.log");
        }
        catch { }
    }

    public event EventHandler<ActivityEntry>? EntryWritten;
    public IReadOnlyList<ActivityEntry> Entries => _entries.ToArray();

    public void Info(string message, string? serviceName = null) => Write("INFO", message, serviceName);
    public void Success(string message, string? serviceName = null) => Write("SUCCESS", message, serviceName);
    public void Error(string message, string? serviceName = null) => Write("ERROR", message, serviceName);

    private void Write(string level, string message, string? serviceName)
    {
        var entry = new ActivityEntry(DateTimeOffset.Now, level, message, serviceName);
        _entries.Enqueue(entry);

        if (_logFile is not null)
        {
            try
            {
                lock (_fileGate)
                {
                    RotateIfNeeded(_logFile);
                    var service = serviceName is null ? string.Empty : $" [{serviceName}]";
                    File.AppendAllText(
                        _logFile,
                        $"{entry.Timestamp:yyyy-MM-dd'T'HH:mm:ss.fffzzz} {level}{service} {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // The in-memory activity view remains available if file logging fails.
            }
        }

        EntryWritten?.Invoke(this, entry);
    }

    private static void RotateIfNeeded(string logFile)
    {
        if (!File.Exists(logFile) || new FileInfo(logFile).Length < MaxLogBytes) return;
        for (var index = 3; index >= 1; index--)
        {
            var source = index == 1 ? logFile : $"{logFile}.{index - 1}";
            var destination = $"{logFile}.{index}";
            if (File.Exists(destination)) File.Delete(destination);
            if (File.Exists(source)) File.Move(source, destination);
        }
    }

    private static void EnsureProtectedDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
