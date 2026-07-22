namespace NodePilot.Engine.PowerShell;

/// <summary>
/// Process-global serialization point for every child-process creation in this process that opens
/// <b>inheritable</b> handles (the isolated launcher's anonymous stdout/stderr pipes, and .NET
/// <c>Process.Start</c> with redirected streams).
///
/// <para>
/// Why this exists: handle inheritance is a per-process, global property. When one launch has an
/// inheritable pipe write-handle momentarily open (e.g. <see cref="IsolatedProcessLauncher"/> between
/// creating the pipes and closing its local client-handle copy), <b>any other</b> concurrent
/// <c>CreateProcess</c>/<c>Process.Start</c> with <c>bInheritHandles:true</c> that does NOT restrict
/// inheritance via <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> will inherit that write-handle. The
/// unrelated child then holds the pipe write-end open, so the isolated reader never reaches EOF and
/// <c>ReadToEndAsync</c> blocks forever — the step Task never completes and the whole execution hangs
/// in <c>Running</c>.
/// </para>
///
/// <para>
/// The isolated launcher's own <c>HANDLE_LIST</c> only controls what <i>its</i> child inherits; it
/// cannot stop <i>other</i> spawns from inheriting <i>our</i> handles. Serializing every
/// inheritable-handle spawn behind this single lock guarantees the inheritable window of one launch
/// never overlaps another <c>CreateProcess</c>, so no cross-inheritance can occur between spawns this
/// process controls. Process creation is sub-millisecond, so the lock costs negligible throughput.
/// </para>
///
/// <para>
/// Residual: a <c>Process.Start</c> executed <i>inside</i> a user PowerShell script (e.g. a
/// <c>startProgram</c> body or a user <c>Start-Job</c>) runs in the runspace and cannot take this
/// lock. That residual is covered by the bounded post-exit drain in
/// <see cref="ProcessExecutionEngine"/>, which never waits forever once the root process has exited
/// and its job tree has been terminated.
/// </para>
/// </summary>
internal static class ProcessSpawnCoordinator
{
    /// <summary>
    /// The critical section is short and fully synchronous at every call site (native
    /// <c>CreateProcess</c> / <c>Process.Start</c>), so a monitor lock — never held across an
    /// <c>await</c> — is the right primitive.
    /// </summary>
    public static readonly object Gate = new();
}
