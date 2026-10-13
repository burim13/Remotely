using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Remotely.Host.Interop;

namespace Remotely.Host.Service;

/// <summary>
/// Starts a process inside a given Windows session as SYSTEM. A service lives in session 0 and cannot see any user's desktop, so it borrows
/// the token of winlogon.exe in the target session (a SYSTEM token already placed in that session) and calls CreateProcessAsUser.
/// Only works when the caller is itself SYSTEM.
/// </summary>
internal static class SessionLauncher
{
    public static int StartInSession(uint sessionId, string commandLine)
    {
        var winlogon = FindWinlogon(sessionId) ?? throw new InvalidOperationException($"winlogon.exe not found in session {sessionId}.");
        IntPtr process = IntPtr.Zero, token = IntPtr.Zero, primary = IntPtr.Zero, environment = IntPtr.Zero;
        try
        {
            process = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION, false, (uint)winlogon);
            if (process == IntPtr.Zero) throw Fail("OpenProcess(winlogon)");
            if (!Native.OpenProcessToken(process, Native.TOKEN_DUPLICATE | Native.TOKEN_QUERY | Native.TOKEN_ASSIGN_PRIMARY, out token))
                throw Fail("OpenProcessToken(winlogon)");
            if (!Native.DuplicateTokenEx(token, Native.MAXIMUM_ALLOWED, IntPtr.Zero, Native.SecurityImpersonation, Native.TokenPrimary, out primary))
                throw Fail("DuplicateTokenEx");
            if (!Native.CreateEnvironmentBlock(out environment, primary, false)) environment = IntPtr.Zero;

            var startup = new STARTUPINFO { lpDesktop = @"winsta0\default" };
            startup.cb = Marshal.SizeOf<STARTUPINFO>();
            var flags = Native.CREATE_NO_WINDOW | (environment != IntPtr.Zero ? Native.CREATE_UNICODE_ENVIRONMENT : 0);
            if (!Native.CreateProcessAsUserW(primary, null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, environment,
                    Environment.SystemDirectory, ref startup, out var info))
                throw Fail("CreateProcessAsUser");
            Native.CloseHandle(info.hThread);
            Native.CloseHandle(info.hProcess);
            return info.dwProcessId;
        }
        finally
        {
            if (environment != IntPtr.Zero) Native.DestroyEnvironmentBlock(environment);
            if (primary != IntPtr.Zero) Native.CloseHandle(primary);
            if (token != IntPtr.Zero) Native.CloseHandle(token);
            if (process != IntPtr.Zero) Native.CloseHandle(process);
        }
    }

    public static int? FindWinlogon(uint sessionId)
    {
        var all = Process.GetProcessesByName("winlogon");
        try
        {
            return all.FirstOrDefault(p => (uint)p.SessionId == sessionId)?.Id;
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
    }

    private static InvalidOperationException Fail(string api)
    {
        var code = Marshal.GetLastWin32Error();
        return new InvalidOperationException($"{api} failed: {new Win32Exception(code).Message} (error {code}).");
    }
}
