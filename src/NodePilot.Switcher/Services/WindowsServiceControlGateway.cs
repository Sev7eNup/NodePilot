using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NodePilot.Switcher.Models;

namespace NodePilot.Switcher.Services;

internal sealed class WindowsServiceControlGateway : IServiceControlGateway
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const int ScStatusProcessInfo = 0;
    private const int ServiceConfigDelayedAutoStartInfo = 3;
    private const uint ServiceNoChange = 0xffffffff;
    private const uint ServiceAutoStart = 2;
    private const uint ServiceDemandStart = 3;
    private const uint ServiceDisabled = 4;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorServiceAlreadyRunning = 1056;
    private const uint ErrorServiceDoesNotExist = 1060;
    private const uint ErrorServiceNotActive = 1062;
    private const uint ProcessTerminate = 0x0001;

    public ServiceSnapshot? TryGetService(string serviceName)
    {
        using var scm = OpenScManager();
        using var service = OpenServiceW(scm, serviceName, ServiceQueryConfig | ServiceQueryStatus);
        if (service.IsInvalid)
        {
            var error = (uint)Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist) return null;
            throw Win32(error, $"Could not open service '{serviceName}'.");
        }

        return ReadSnapshot(service, serviceName);
    }

    public Task SetStartModeAsync(
        string serviceName,
        ServiceStartMode mode,
        bool delayedAutoStart,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scm = OpenScManager();
        using var service = OpenRequiredService(scm, serviceName, ServiceChangeConfig | ServiceQueryConfig);
        var nativeMode = mode switch
        {
            ServiceStartMode.Automatic => ServiceAutoStart,
            ServiceStartMode.Manual => ServiceDemandStart,
            ServiceStartMode.Disabled => ServiceDisabled,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported service start mode."),
        };

        if (!ChangeServiceConfigW(
                service, ServiceNoChange, nativeMode, ServiceNoChange,
                null, null, IntPtr.Zero, null, null, null, null))
        {
            throw Win32((uint)Marshal.GetLastWin32Error(), $"Could not change start mode for '{serviceName}'.");
        }

        var delayed = new ServiceDelayedAutoStartInfo { DelayedAutoStart = mode == ServiceStartMode.Automatic && delayedAutoStart };
        if (!ChangeServiceConfig2W(service, ServiceConfigDelayedAutoStartInfo, ref delayed))
        {
            throw Win32((uint)Marshal.GetLastWin32Error(), $"Could not change delayed start for '{serviceName}'.");
        }
    }, cancellationToken);

    public Task StartAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        using var scm = OpenScManager();
        using var service = OpenRequiredService(scm, serviceName, ServiceStart | ServiceQueryStatus);
        var status = ReadStatus(service);
        if (status.State == ServiceRuntimeState.Running) return;

        if (!StartServiceW(service, 0, null))
        {
            var error = (uint)Marshal.GetLastWin32Error();
            if (error != ErrorServiceAlreadyRunning)
                throw Win32(error, $"Could not start service '{serviceName}'.");
        }

        await WaitForStateAsync(serviceName, ServiceRuntimeState.Running, timeout, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    public Task StopAsync(
        string serviceName,
        TimeSpan gracefulTimeout,
        TimeSpan forcedTimeout,
        CancellationToken cancellationToken) => Task.Run(async () =>
    {
        using (var scm = OpenScManager())
        using (var service = OpenRequiredService(scm, serviceName, ServiceStop | ServiceQueryStatus))
        {
            var status = ReadStatus(service);
            if (status.State == ServiceRuntimeState.Stopped) return;

            if (!ControlService(service, ServiceControlStop, out _))
            {
                var error = (uint)Marshal.GetLastWin32Error();
                if (error != ErrorServiceNotActive)
                    throw Win32(error, $"Could not stop service '{serviceName}'.");
            }
        }

        if (await WaitForStateOrTimeoutAsync(serviceName, ServiceRuntimeState.Stopped, gracefulTimeout, cancellationToken)
                .ConfigureAwait(false))
            return;

        ForceTerminateVerifiedServiceProcess(serviceName);
        await WaitForStateAsync(serviceName, ServiceRuntimeState.Stopped, forcedTimeout, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    public Task ForceStopAsync(
        string serviceName,
        TimeSpan timeout,
        CancellationToken cancellationToken) => Task.Run(async () =>
    {
        ForceTerminateVerifiedServiceProcess(serviceName);
        await WaitForStateAsync(serviceName, ServiceRuntimeState.Stopped, timeout, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    private static void ForceTerminateVerifiedServiceProcess(string serviceName)
    {
        using var scm = OpenScManager();
        using var service = OpenRequiredService(scm, serviceName, ServiceQueryStatus);
        var first = ReadStatus(service);
        if (first.State == ServiceRuntimeState.Stopped) return;
        if (first.ProcessId <= 0)
            throw new InvalidOperationException($"Service '{serviceName}' did not expose a process id after the stop timeout.");

        using var process = OpenProcess(ProcessTerminate, false, (uint)first.ProcessId);
        if (process.IsInvalid)
            throw Win32((uint)Marshal.GetLastWin32Error(), $"Could not open the timed-out process for '{serviceName}'.");

        var verified = ReadStatus(service);
        if (verified.State == ServiceRuntimeState.Stopped) return;
        if (verified.ProcessId != first.ProcessId)
            throw new InvalidOperationException($"Service '{serviceName}' changed process id during forced-stop verification.");

        if (!TerminateProcess(process, 1))
            throw Win32((uint)Marshal.GetLastWin32Error(), $"Could not terminate the timed-out process for '{serviceName}'.");
    }

    private static async Task WaitForStateAsync(
        string serviceName,
        ServiceRuntimeState desired,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (await WaitForStateOrTimeoutAsync(serviceName, desired, timeout, cancellationToken).ConfigureAwait(false)) return;
        var current = ReadRequiredSnapshot(serviceName);
        throw new TimeoutException(
            $"Service '{serviceName}' did not reach {desired} within {timeout.TotalSeconds:0} seconds (current: {current.State}).");
    }

    private static async Task<bool> WaitForStateOrTimeoutAsync(
        string serviceName,
        ServiceRuntimeState desired,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = ReadRequiredSnapshot(serviceName);
            if (current.State == desired) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static ServiceSnapshot ReadRequiredSnapshot(string serviceName)
    {
        var gateway = new WindowsServiceControlGateway();
        return gateway.TryGetService(serviceName)
               ?? throw new InvalidOperationException($"Service '{serviceName}' disappeared while it was being controlled.");
    }

    private static ServiceSnapshot ReadSnapshot(SafeServiceHandle service, string serviceName)
    {
        var status = ReadStatus(service);
        QueryServiceConfigW(service, IntPtr.Zero, 0, out var bytesNeeded);
        var error = (uint)Marshal.GetLastWin32Error();
        if (bytesNeeded == 0 || error != ErrorInsufficientBuffer)
            throw Win32(error, $"Could not query configuration for '{serviceName}'.");

        var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
        try
        {
            if (!QueryServiceConfigW(service, buffer, bytesNeeded, out _))
                throw Win32((uint)Marshal.GetLastWin32Error(), $"Could not query configuration for '{serviceName}'.");

            var config = Marshal.PtrToStructure<QueryServiceConfig>(buffer);
            var dependencies = ReadMultiString(config.Dependencies);
            return new ServiceSnapshot(
                serviceName,
                Marshal.PtrToStringUni(config.BinaryPathName) ?? string.Empty,
                status.State,
                MapStartMode(config.StartType),
                ReadDelayedAutoStart(service),
                status.ProcessId,
                dependencies);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (ServiceRuntimeState State, int ProcessId) ReadStatus(SafeServiceHandle service)
    {
        var size = Marshal.SizeOf<ServiceStatusProcess>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, (uint)size, out _))
                throw Win32((uint)Marshal.GetLastWin32Error(), "Could not query service status.");
            var status = Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
            return (MapState(status.CurrentState), checked((int)status.ProcessId));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool ReadDelayedAutoStart(SafeServiceHandle service)
    {
        var size = Marshal.SizeOf<ServiceDelayedAutoStartInfo>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            return QueryServiceConfig2W(service, ServiceConfigDelayedAutoStartInfo, buffer, (uint)size, out _)
                   && Marshal.PtrToStructure<ServiceDelayedAutoStartInfo>(buffer).DelayedAutoStart;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<string> ReadMultiString(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return [];
        var values = new List<string>();
        var offset = 0;
        while (true)
        {
            var value = Marshal.PtrToStringUni(IntPtr.Add(pointer, offset * sizeof(char)));
            if (string.IsNullOrEmpty(value)) break;
            values.Add(value);
            offset += value.Length + 1;
        }
        return values;
    }

    private static ServiceRuntimeState MapState(uint value) => value switch
    {
        1 => ServiceRuntimeState.Stopped,
        2 => ServiceRuntimeState.StartPending,
        3 => ServiceRuntimeState.StopPending,
        4 => ServiceRuntimeState.Running,
        5 => ServiceRuntimeState.ContinuePending,
        6 => ServiceRuntimeState.PausePending,
        7 => ServiceRuntimeState.Paused,
        _ => ServiceRuntimeState.Unknown,
    };

    private static ServiceStartMode MapStartMode(uint value) => value switch
    {
        ServiceAutoStart => ServiceStartMode.Automatic,
        ServiceDemandStart => ServiceStartMode.Manual,
        ServiceDisabled => ServiceStartMode.Disabled,
        _ => ServiceStartMode.Unknown,
    };

    private static SafeServiceHandle OpenScManager()
    {
        var handle = OpenSCManagerW(null, null, ScManagerConnect);
        if (handle.IsInvalid)
            throw Win32((uint)Marshal.GetLastWin32Error(), "Could not open Windows Service Control Manager.");
        return handle;
    }

    private static SafeServiceHandle OpenRequiredService(SafeServiceHandle scm, string name, uint access)
    {
        var handle = OpenServiceW(scm, name, access);
        if (handle.IsInvalid)
            throw Win32((uint)Marshal.GetLastWin32Error(), $"Could not open service '{name}'.");
        return handle;
    }

    private static Win32Exception Win32(uint error, string message) => new((int)error, message);

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfig
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDelayedAutoStartInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DelayedAutoStart;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeServiceHandle OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeServiceHandle OpenServiceW(SafeServiceHandle scm, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle service,
        int infoLevel,
        IntPtr buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(
        SafeServiceHandle service,
        IntPtr queryServiceConfig,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2W(
        SafeServiceHandle service,
        int infoLevel,
        IntPtr buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfigW(
        SafeServiceHandle service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(
        SafeServiceHandle service,
        int infoLevel,
        ref ServiceDelayedAutoStartInfo info);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(SafeServiceHandle service, uint argCount, string[]? args);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(SafeServiceHandle service, uint control, out ServiceStatus status);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
