using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Remotely.Host.Interop;

namespace Remotely.Host.Helper;

/// <summary>
/// One dedicated thread that owns all desktop switching. Before each job it attaches itself to whichever desktop currently receives input
/// (the user's "Default", or "Winlogon" for the lock screen, sign-in and UAC). SetThreadDesktop applies per thread and fails if the thread owns
/// windows, so nothing else (GDI+, UI) may ever run here.
/// </summary>
internal sealed class DesktopThread : IDisposable
{
    private readonly BlockingCollection<Action> _jobs = [];
    private readonly Thread _thread;
    private IntPtr _current;

    public DesktopThread()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Remotely desktop" };
        _thread.Start();
    }

    public Task<T> RunAsync<T>(Func<string, T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Add(() =>
        {
            try { done.SetResult(work(SwitchToInputDesktop())); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    private void Loop()
    {
        foreach (var job in _jobs.GetConsumingEnumerable()) job();
    }

    private string SwitchToInputDesktop()
    {
        var input = Native.OpenInputDesktop(0, false, Native.GENERIC_ALL);
        if (input == IntPtr.Zero) throw Fail("OpenInputDesktop");
        if (!Native.SetThreadDesktop(input))
        {
            var error = Fail("SetThreadDesktop");
            Native.CloseDesktop(input);
            throw error;
        }
        if (_current != IntPtr.Zero) Native.CloseDesktop(_current);
        _current = input;
        return Native.DesktopName(input);
    }

    public static InvalidOperationException Fail(string api)
    {
        var code = Marshal.GetLastWin32Error();
        return new InvalidOperationException($"{api} failed: {new Win32Exception(code).Message} (error {code}).");
    }

    public void Dispose()
    {
        _jobs.CompleteAdding();
        _thread.Join(2000);
    }
}
