using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Remotely.Host.Interop;
using Remotely.Unattended;

namespace Remotely.Host.Unattended;

// Everything in this file talks to Windows and cannot run in the Linux unit tests. What it decides is in Unattended/Core; what it does is shown by CI on Windows (docs/unattended.md).

internal static class UnattendedNative
{
    [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetNamedPipeClientSessionId(IntPtr pipe, out uint sessionId);

    [StructLayout(LayoutKind.Sequential)]
    public struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;   // the first member of the union; the other union members are as large, so the struct is padded to their size
        public uint LocalizedReasonId;
        public uint ReasonStringCount;
        public IntPtr ReasonStrings;
    }

    public const uint PowerRequestContextVersion = 0, PowerRequestContextSimpleString = 0x1;
    public const int PowerRequestDisplayRequired = 0, PowerRequestSystemRequired = 1;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool PowerSetRequest(IntPtr request, int requestType);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool PowerClearRequest(IntPtr request, int requestType);
}

/// <summary>Makes the listening instance of the pipe, with the access list from <see cref="PipeAccessPolicy"/>.</summary>
internal static class WindowsPipe
{
    public static NamedPipeServerStream Create(string? userSid, bool first)
    {
        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm(PipeAccessPolicy.Sddl(userSid));
        // FirstPipeInstance: creating the first instance fails if some other program already owns this name, instead of silently joining it.
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(UnattendedPipe.Name, PipeDirection.InOut, UnattendedPipe.MaxServerInstances, PipeTransmissionMode.Byte, options, 0, 0, security);
    }
}

/// <summary>Reads the user and session of the connected client (the second check, after Windows has already applied the pipe's access list).</summary>
internal sealed class WindowsPeerCheck : IPeerCheck
{
    private static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);

    public PeerVerdict Check(NamedPipeServerStream pipe, PeerContext expected)
    {
        uint? session = UnattendedNative.GetNamedPipeClientSessionId(pipe.SafePipeHandle.DangerousGetHandle(), out var id) ? id : null;
        PeerInfo? peer = null;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            peer = new PeerInfo(identity.User?.Value, session, identity.Groups?.Contains(Network) == true);
        });
        return PipeAccessPolicy.Decide(peer ?? new PeerInfo(null, session, false), expected.UserSid, expected.Session);
    }
}

/// <summary>The user signed in at the console, found with the same SYSTEM privilege the service already uses to start the screen helper.</summary>
internal sealed class ConsoleUser
{
    private readonly object _gate = new();
    private PeerContext _cached = new(null, null);
    private DateTime _at = DateTime.MinValue;

    public PeerContext Current()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _at < TimeSpan.FromSeconds(1)) return _cached;
            _cached = Read();
            _at = DateTime.UtcNow;
            return _cached;
        }
    }

    private static PeerContext Read()
    {
        var session = Native.WTSGetActiveConsoleSessionId();
        if (session == Native.NoSession) return new(null, null);
        if (!UnattendedNative.WTSQueryUserToken(session, out var token)) return new(null, session);   // nobody is signed in (the sign-in screen)
        try
        {
            using var identity = new WindowsIdentity(token);
            return new(identity.User?.Value, session);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException)
        {
            return new(null, session);
        }
        finally { Native.CloseHandle(token); }
    }
}

/// <summary>"Do not sleep" for Windows while someone is looking at this PC: the system and the display stay on.</summary>
internal sealed class PowerRequest(Action<string> log) : IPowerRequest, IDisposable
{
    private IntPtr _handle;

    public bool Acquire()
    {
        try
        {
            if (_handle == IntPtr.Zero)
            {
                var reason = Marshal.StringToHGlobalUni("Remotely: someone is viewing this PC");
                try
                {
                    var context = new UnattendedNative.ReasonContext { Version = UnattendedNative.PowerRequestContextVersion, Flags = UnattendedNative.PowerRequestContextSimpleString, SimpleReasonString = reason };
                    _handle = UnattendedNative.PowerCreateRequest(ref context);
                    if (_handle == IntPtr.Zero || _handle == new IntPtr(-1)) { _handle = IntPtr.Zero; throw new Win32Exception(Marshal.GetLastWin32Error()); }
                }
                finally { Marshal.FreeHGlobal(reason); }   // Windows copies the text when the request is created
            }
            if (!UnattendedNative.PowerSetRequest(_handle, UnattendedNative.PowerRequestSystemRequired) || !UnattendedNative.PowerSetRequest(_handle, UnattendedNative.PowerRequestDisplayRequired))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            log("Keeping this PC awake while someone is viewing it.");
            return true;
        }
        catch (Win32Exception ex)
        {
            log("Could not ask Windows to stay awake: " + ex.Message);
            return false;
        }
    }

    public void Release()
    {
        if (_handle == IntPtr.Zero) return;
        UnattendedNative.PowerClearRequest(_handle, UnattendedNative.PowerRequestSystemRequired);
        UnattendedNative.PowerClearRequest(_handle, UnattendedNative.PowerRequestDisplayRequired);
        log("No one is viewing this PC any more; Windows may sleep again.");
    }

    public void Dispose()
    {
        Release();
        if (_handle != IntPtr.Zero) { Native.CloseHandle(_handle); _handle = IntPtr.Zero; }
    }
}

/// <summary>The Windows Firewall rules that let the service (not the app) receive same-network connections. The app's own rules, made by the installer, name Remotely.exe and do not cover the service.</summary>
internal static class LanFirewall
{
    public const string TcpRule = "Remotely service same-network (TCP)";
    public const string UdpRule = "Remotely service same-network (UDP)";

    public static bool Add(string exePath, TextWriter output)
    {
        var ok = true;
        foreach (var (name, protocol, port) in new[] { (TcpRule, "TCP", Remotely.Core.Lan.LanNetwork.TcpPort), (UdpRule, "UDP", Remotely.Core.Lan.LanNetwork.DiscoveryPort) })
        {
            Commands.Installer.Run("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={name}");   // replace an older rule instead of adding a second one
            var (code, text) = Commands.Installer.Run("netsh.exe", "advfirewall", "firewall", "add", "rule", $"name={name}", "dir=in", "action=allow", $"program={exePath}",
                $"protocol={protocol}", $"localport={port}", "profile=private,domain");
            if (code != 0) { output.WriteLine($"Could not add the firewall rule '{name}': {text.Trim()}"); ok = false; }
        }
        return ok;
    }

    public static void Remove()
    {
        foreach (var name in new[] { TcpRule, UdpRule })
            Commands.Installer.Run("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={name}");
    }

    public static bool Exists() => Commands.Installer.Run("netsh.exe", "advfirewall", "firewall", "show", "rule", $"name={TcpRule}").Code == 0;
}

/// <summary>The registry value behind Ctrl+Alt+Del from a service. Reading needs no rights; writing needs an administrator.</summary>
internal static class SasRegistry
{
    public static int? Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(SasPolicy.KeyPath);
        return key?.GetValue(SasPolicy.ValueName) is int value ? value : null;
    }

    public static void Allow()
    {
        using var key = Registry.LocalMachine.CreateSubKey(SasPolicy.KeyPath);
        key.SetValue(SasPolicy.ValueName, 1, RegistryValueKind.DWord);
    }

    /// <summary>Removes the value, which puts the policy back to "not configured" (services may not send Ctrl+Alt+Del).</summary>
    public static void Deny()
    {
        using var key = Registry.LocalMachine.OpenSubKey(SasPolicy.KeyPath, writable: true);
        key?.DeleteValue(SasPolicy.ValueName, throwOnMissingValue: false);
    }
}
