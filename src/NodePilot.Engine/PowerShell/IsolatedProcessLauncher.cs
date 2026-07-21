using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NodePilot.Engine.PowerShell;

/// <summary>
/// Launches a child process **race-free inside a Windows Job Object** and returns a handle for
/// awaiting exit, reading stdout/stderr, and tearing the whole tree down.
///
/// The job is assigned <b>at creation time</b> via <c>STARTUPINFOEX</c> +
/// <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>, so the child is contained before its first instruction
/// runs — there is no <c>Process.Start()</c> → <c>AssignProcessToJobObject()</c> race. The job
/// carries <c>KILL_ON_JOB_CLOSE</c> (host death / handle close ⇒ kernel reaps the whole tree, no
/// orphans) + <c>DIE_ON_UNHANDLED_EXCEPTION</c> (a native crash fails fast instead of hanging on a
/// WER dialog), plus optional aggregate <c>JOB_MEMORY</c> and <c>ACTIVE_PROCESS</c> caps.
///
/// Handle inheritance is restricted to exactly {NUL-stdin, stdout-pipe, stderr-pipe} via
/// <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>, so <c>bInheritHandles:true</c> does not leak unrelated
/// inheritable handles of the API host into the child.
///
/// Windows-only by construction (the orchestrator targets <c>net10.0-windows</c>); callers guard
/// with <see cref="OperatingSystem.IsWindows"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class IsolatedProcessLauncher
{
    internal const int DefaultMemoryLimitMb = 1024;
    internal const int DefaultMaxProcesses = 16;

    // --- creation flags ---
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint STARTF_USESTDHANDLES = 0x00000100;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;

    // --- CreateFile ---
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    // --- proc-thread attributes (ProcThreadAttributeValue macro expansions) ---
    private static readonly nuint PROC_THREAD_ATTRIBUTE_JOB_LIST = 0x0002000D;
    private static readonly nuint PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;

    // --- job object limit flags ---
    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
    private const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;

    /// <summary>
    /// Creates the job, opens the pipes, and starts <paramref name="executable"/> with
    /// <paramref name="arguments"/> already inside the job. Throws <see cref="Win32Exception"/> on
    /// any native failure (caller turns it into a failed step result).
    /// </summary>
    public static LaunchedIsolatedProcess Launch(
        string executable,
        string arguments,
        string? workingDirectory,
        ProcessIsolationLimits? limits)
    {
        SafeJobHandle? job = null;
        AnonymousPipeServerStream? outPipe = null;
        AnonymousPipeServerStream? errPipe = null;
        SafeFileHandle? nulIn = null;
        IntPtr attrList = IntPtr.Zero;
        IntPtr pJob = IntPtr.Zero;
        IntPtr pHandles = IntPtr.Zero;
        IntPtr lpCommandLine = IntPtr.Zero;
        var attrInitialized = false;

        try
        {
            job = CreateJob(limits);

            // PipeDirection.In ⇒ the parent (server) reads; the child inherits the write end via
            // ClientSafePipeHandle. Inheritable so it survives into the child.
            outPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            errPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

            nulIn = OpenInheritableNul();

            var hStdIn = nulIn.DangerousGetHandle();
            var hStdOut = outPipe.ClientSafePipeHandle.DangerousGetHandle();
            var hStdErr = errPipe.ClientSafePipeHandle.DangerousGetHandle();

            // --- attribute list (2 attributes: JOB_LIST + HANDLE_LIST) ---
            nuint size = 0;
            // First call is expected to fail (ERROR_INSUFFICIENT_BUFFER) and only fills `size`.
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attrList = Marshal.AllocHGlobal((int)size);
            if (!InitializeProcThreadAttributeList(attrList, 2, 0, ref size))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList failed");
            attrInitialized = true;

            // cbSize is the BYTE size of the value, not a count: one HANDLE for JOB_LIST,
            // three for HANDLE_LIST. The value buffers must stay alive until DeleteProcThreadAttributeList.
            pJob = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(pJob, job.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, pJob, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute(JOB_LIST) failed");

            pHandles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(pHandles, 0, hStdIn);
            Marshal.WriteIntPtr(pHandles, IntPtr.Size, hStdOut);
            Marshal.WriteIntPtr(pHandles, IntPtr.Size * 2, hStdErr);
            if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, pHandles, (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute(HANDLE_LIST) failed");

            var startupInfo = new STARTUPINFOEX();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            startupInfo.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            startupInfo.StartupInfo.hStdInput = hStdIn;
            startupInfo.StartupInfo.hStdOutput = hStdOut;
            startupInfo.StartupInfo.hStdError = hStdErr;
            startupInfo.lpAttributeList = attrList;

            // lpApplicationName = null ⇒ CreateProcess parses argv[0] from the (quoted) command
            // line. The buffer must be writable (CreateProcessW may modify it in place), so we
            // hand it our own Unicode HGlobal, never a read-only managed string.
            var commandLine = "\"" + executable + "\" " + arguments;
            lpCommandLine = Marshal.StringToHGlobalUni(commandLine);

            var created = CreateProcess(
                lpApplicationName: null,
                lpCommandLine: lpCommandLine,
                lpProcessAttributes: IntPtr.Zero,
                lpThreadAttributes: IntPtr.Zero,
                bInheritHandles: true,
                dwCreationFlags: EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW,
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: workingDirectory,
                lpStartupInfo: in startupInfo,
                lpProcessInformation: out var pi);

            if (!created)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CreateProcess({executable}) failed");

            // Thread handle is unused — close immediately. Wrap the process handle so it is
            // released deterministically (and on finalization as a backstop).
            CloseHandle(pi.hThread);
            var processHandle = new SafeProcessHandle(pi.hProcess, ownsHandle: true);

            // Parent no longer needs the inherited write-ends; closing its copies lets the read
            // side reach EOF once the child tree closes its copies.
            outPipe.DisposeLocalCopyOfClientHandle();
            errPipe.DisposeLocalCopyOfClientHandle();

            var launched = new LaunchedIsolatedProcess(job, processHandle, pi.dwProcessId, outPipe, errPipe);
            // Ownership transferred — stop the finally from disposing them.
            job = null;
            outPipe = null;
            errPipe = null;
            return launched;
        }
        finally
        {
            if (attrInitialized) DeleteProcThreadAttributeList(attrList);
            if (attrList != IntPtr.Zero) Marshal.FreeHGlobal(attrList);
            if (pJob != IntPtr.Zero) Marshal.FreeHGlobal(pJob);
            if (pHandles != IntPtr.Zero) Marshal.FreeHGlobal(pHandles);
            if (lpCommandLine != IntPtr.Zero) Marshal.FreeHGlobal(lpCommandLine);
            nulIn?.Dispose();
            errPipe?.Dispose();
            outPipe?.Dispose();
            job?.Dispose();
        }
    }

    private static SafeJobHandle CreateJob(ProcessIsolationLimits? limits)
    {
        var effectiveLimits = EffectiveLimits(limits);
        var raw = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (raw == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed");

        var job = new SafeJobHandle(raw);
        try
        {
            var info = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
            var flags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;

            if (effectiveLimits?.MaxProcesses is { } mp && mp > 0)
            {
                flags |= JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
                info.BasicLimitInformation.ActiveProcessLimit = (uint)mp;
            }
            if (effectiveLimits?.MemoryLimitMb is { } mb && mb > 0)
            {
                flags |= JOB_OBJECT_LIMIT_JOB_MEMORY;
                info.JobMemoryLimit = (UIntPtr)((ulong)mb * 1024 * 1024);
            }
            info.BasicLimitInformation.LimitFlags = flags;

            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, in info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetInformationJobObject failed");

            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    internal static ProcessIsolationLimits? EffectiveLimits(ProcessIsolationLimits? limits)
        => limits is null
            ? null
            : limits with
            {
                MemoryLimitMb = limits.MemoryLimitMb is > 0 ? limits.MemoryLimitMb : DefaultMemoryLimitMb,
                MaxProcesses = limits.MaxProcesses is > 0 ? limits.MaxProcesses : DefaultMaxProcesses,
            };

    private static SafeFileHandle OpenInheritableNul()
    {
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = IntPtr.Zero,
            bInheritHandle = 1,
        };
        var handle = CreateFile("NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, in sa, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateFile(NUL) failed");
        return handle;
    }

    // ===================== P/Invoke =====================

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateJobObjectW(IntPtr lpJobAttributes, IntPtr lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobHandle hJob,
        int jobObjectInformationClass,
        in JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
        int cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateJobObject(SafeJobHandle hJob, uint uExitCode);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        in SECURITY_ATTRIBUTES lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref nuint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, nuint attribute, IntPtr lpValue, nuint cbSize,
        IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(
        string? lpApplicationName,
        IntPtr lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        in STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    // ===================== native structs =====================

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    /// <summary>Owns a job-object HANDLE; closing it triggers KILL_ON_JOB_CLOSE on the tree.</summary>
    internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}

/// <summary>
/// Handle to a process launched by <see cref="IsolatedProcessLauncher"/>. Owns the job, the process
/// handle, and the stdout/stderr pipe readers. Disposing closes the job (KILL_ON_JOB_CLOSE reaps any
/// survivors), so the caller must read output and the exit code BEFORE disposing.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class LaunchedIsolatedProcess : IDisposable
{
    private readonly IsolatedProcessLauncher.SafeJobHandle _job;
    private readonly SafeProcessHandle _process;

    /// <summary>OS process id of the launched root process.</summary>
    public int ProcessId { get; }

    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }

    internal LaunchedIsolatedProcess(
        IsolatedProcessLauncher.SafeJobHandle job,
        SafeProcessHandle process,
        int processId,
        AnonymousPipeServerStream outPipe,
        AnonymousPipeServerStream errPipe)
    {
        _job = job;
        _process = process;
        ProcessId = processId;
        StandardOutput = new StreamReader(outPipe, Encoding.UTF8);
        StandardError = new StreamReader(errPipe, Encoding.UTF8);
    }

    /// <summary>Completes when the root process exits, or throws OCE if <paramref name="ct"/> fires.</summary>
    public async Task WaitForExitAsync(CancellationToken ct)
    {
        if (_process.IsInvalid) return;

        // The process handle is signalled on exit; wrap it (non-owning) in a WaitHandle and
        // register a one-shot ThreadPool wait so we never block a pool thread for the run.
        using var waitHandle = new ManualResetEvent(false);
        var original = waitHandle.SafeWaitHandle;
        waitHandle.SafeWaitHandle = new SafeWaitHandle(_process.DangerousGetHandle(), ownsHandle: false);
        original.Dispose();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle, (_, _) => tcs.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await using (ct.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), tcs).ConfigureAwait(false))
                await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
            GC.KeepAlive(_process);
        }
    }

    /// <summary>Reads the root process exit code. Call only after <see cref="WaitForExitAsync"/>.</summary>
    public int GetExitCode()
    {
        if (!IsolatedProcessLauncher.GetExitCodeProcess(_process, out var code))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetExitCodeProcess failed");
        return unchecked((int)code);
    }

    /// <summary>Synchronously terminates the whole job tree (root + any surviving children).</summary>
    public void Terminate()
    {
        if (!_job.IsInvalid && !_job.IsClosed)
            IsolatedProcessLauncher.TerminateJobObject(_job, 1);
    }

    public void Dispose()
    {
        StandardOutput.Dispose();
        StandardError.Dispose();
        _process.Dispose();
        _job.Dispose(); // KILL_ON_JOB_CLOSE backstop
    }
}
