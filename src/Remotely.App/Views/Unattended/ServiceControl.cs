using System.Runtime.InteropServices;
using Remotely.Unattended;

namespace Remotely.App.Views.Unattended;

/// <summary>
/// Asks the Windows service manager about the Remotely service. Any user may do this; only starting, stopping, installing and removing need administrator rights and go through
/// <see cref="ElevatedHost"/>. It uses the service manager's own API rather than the text of <c>sc.exe</c>, whose output depends on the language of Windows.
/// </summary>
internal static class ServiceControl
{
    public const string ServiceName = "Remotely.Host";

    public sealed record Info(ServiceState State, uint ProcessId);

    private const uint ScManagerConnect = 0x0001, ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0, ErrorServiceDoesNotExist = 1060;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatusProcess status, uint size, out uint needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);

    public static Info Query()
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return new(ServiceState.Unknown, 0);
        try
        {
            var service = OpenServiceW(manager, ServiceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
                return new(Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist ? ServiceState.NotInstalled : ServiceState.Unknown, 0);
            try
            {
                if (!QueryServiceStatusEx(service, ScStatusProcessInfo, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _)) return new(ServiceState.Unknown, 0);
                return new(status.CurrentState switch
                {
                    1 => ServiceState.Stopped,
                    2 => ServiceState.Starting,
                    3 => ServiceState.Stopping,
                    4 => ServiceState.Running,
                    5 => ServiceState.Starting,   // continue pending
                    6 => ServiceState.Stopping,   // pause pending
                    7 => ServiceState.Paused,
                    _ => ServiceState.Unknown,
                }, status.ProcessId);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
}
