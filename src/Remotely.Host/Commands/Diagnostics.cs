using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Remotely.Host.Helper;
using Remotely.Host.Interop;
using Remotely.Host.Service;

namespace Remotely.Host.Commands;

/// <summary>Prints the facts the prototype depends on, so a failure can be pasted back and read without guessing.</summary>
internal static class Diagnostics
{
    public static int Run(string? capturePath)
    {
        using var me = WindowsIdentity.GetCurrent();
        Console.WriteLine($"OS:               {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Console.WriteLine($"Process arch:     {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Running as:       {me.Name}  (SYSTEM: {me.IsSystem}, administrator: {HostPaths.IsAdministrator()})");
        var consoleSession = Native.WTSGetActiveConsoleSessionId();
        Console.WriteLine($"This session:     {Process.GetCurrentProcess().SessionId}    Console session: {(consoleSession == Native.NoSession ? "none" : consoleSession)}");

        foreach (var p in Process.GetProcessesByName("winlogon"))
        {
            Console.Write($"winlogon.exe:     pid {p.Id}, session {p.SessionId}: ");
            Console.WriteLine(CanBorrowToken(p.Id));
            p.Dispose();
        }

        var input = Native.OpenInputDesktop(0, false, Native.GENERIC_ALL);
        if (input == IntPtr.Zero)
            Console.WriteLine($"Input desktop:    cannot open (error {Marshal.GetLastWin32Error()}); expected unless running as SYSTEM");
        else
        {
            Console.WriteLine($"Input desktop:    {Native.DesktopName(input)}");
            Native.CloseDesktop(input);
        }

        Console.WriteLine($"Mode:             {(HostPaths.UserMode ? "user only (no service)" : "service")}");
        Console.WriteLine($"Data directory:   {HostPaths.DataDir} (exists: {Directory.Exists(HostPaths.DataDir)})");
        Console.WriteLine($"Installed copy:   {HostPaths.InstalledExe} (exists: {File.Exists(HostPaths.InstalledExe)})");
        Console.WriteLine($"Service state:    {Installer.ServiceState()}");

        if (capturePath is not null)
        {
            try
            {
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
                using var desktop = new DesktopThread();
                var frame = new DesktopCapturer(desktop, Console.WriteLine).CaptureAsync(CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllBytes(capturePath, frame.Jpeg);
                Console.WriteLine($"Capture:          OK {frame.Width}x{frame.Height} desktop={frame.Desktop} {frame.Jpeg.Length / 1024} KB -> {capturePath}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Capture:          FAILED: {ex.Message}");
                return 1;
            }
        }
        return 0;
    }

    private static string CanBorrowToken(int pid)
    {
        IntPtr process = IntPtr.Zero, token = IntPtr.Zero;
        try
        {
            process = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION, false, (uint)pid);
            if (process == IntPtr.Zero) return $"cannot open process (error {Marshal.GetLastWin32Error()}); needs SYSTEM";
            if (!Native.OpenProcessToken(process, Native.TOKEN_DUPLICATE | Native.TOKEN_QUERY | Native.TOKEN_ASSIGN_PRIMARY, out token))
                return $"cannot open token (error {Marshal.GetLastWin32Error()})";
            return "token can be borrowed";
        }
        finally
        {
            if (token != IntPtr.Zero) Native.CloseHandle(token);
            if (process != IntPtr.Zero) Native.CloseHandle(process);
        }
    }
}
