using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Remotely.UITests;

/// <summary>Which app is under test: where its settings and crash log live and how it is started for a page.</summary>
internal sealed record AppKind(string Name, string SettingsDir, string SettingsFile, string CrashLog, string[] ExtraArgs)
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static AppKind Viewer { get; } = new("Remotely", Path.Combine(Local, "Remotely"), "viewer.json", Path.Combine(Local, "Remotely", "logs", "app-crash.log"), []);
    public static AppKind Manager { get; } = new("Remotely Server Manager", Path.Combine(Local, "Remotely Server Manager"), "manager.json",
        Path.Combine(Local, "Remotely Server Manager", "logs", "manager-crash.log"), ["--fake"]);

    public string SettingsPath => Path.Combine(SettingsDir, SettingsFile);

    /// <summary>
    /// Writes the settings the test needs before the app starts: the theme, window size and (for the viewer) a fixed PC name, so screenshots do not contain the runner's machine name.
    /// Other keys that are already in the file are kept.
    /// </summary>
    public void WriteSettings(string theme, bool onboardingDone, int scalePercent, Func<JsonObject, JsonObject>? more = null)
    {
        Directory.CreateDirectory(SettingsDir);
        JsonObject o;
        try { o = File.Exists(SettingsPath) ? (JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new JsonObject()) : new JsonObject(); }
        catch (System.Text.Json.JsonException) { o = new JsonObject(); }
        o["Theme"] = theme;
        if (this == Viewer)
        {
            o["OnboardingDone"] = onboardingDone;
            o["DeviceName"] = "UITest-PC";
            var screenW = Native.GetSystemMetrics(Native.SM_CXSCREEN);
            var screenH = Native.GetSystemMetrics(Native.SM_CYSCREEN);
            var width = Math.Min(1100 * scalePercent / 100, screenW - 60);
            var height = Math.Min(760 * scalePercent / 100, screenH - 100);
            o["WindowX"] = 20;
            o["WindowY"] = 20;
            o["WindowWidth"] = width;
            o["WindowHeight"] = height;
            o["WindowMaximized"] = false;
        }
        if (more is not null) o = more(o);
        File.WriteAllText(SettingsPath, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public void ClearSettings()
    {
        try { File.Delete(SettingsPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public string ReadCrashLog()
    {
        try { return File.Exists(CrashLog) ? File.ReadAllText(CrashLog).Trim() : ""; }
        catch (IOException) { return ""; }
    }

    public void ClearCrashLog()
    {
        try { File.Delete(CrashLog); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>One running copy of an app: start it, wait for its window, stop it.</summary>
internal sealed class AppSession : IDisposable
{
    private readonly Process _process;

    private AppSession(Process process, AppKind kind) { _process = process; Kind = kind; }

    public AppKind Kind { get; }
    public int Pid => _process.Id;
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    public static AppSession Start(AppKind kind, string exe, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(Path.GetFullPath(exe)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))! };
        foreach (var a in kind.ExtraArgs.Concat(args)) info.ArgumentList.Add(a);
        return new AppSession(Process.Start(info) ?? throw new InvalidOperationException("the process did not start"), kind);
    }

    /// <summary>Stops every copy of the app that is still running (a previous variant, a hung start); the apps allow one instance per user.</summary>
    public static void KillAll(AppKind kind)
    {
        var exeName = kind == AppKind.Manager ? "Remotely.ServerManager" : "Remotely";
        foreach (var p in Process.GetProcessesByName(exeName))
        {
            try { p.Kill(true); p.WaitForExit(5000); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            p.Dispose();
        }
    }

    /// <summary>
    /// Waits for a visible top-level window of the process with a real size that stays the same for 600 ms. Null when the process exits first or the time runs out; the reason is in
    /// <paramref name="why"/>.
    /// </summary>
    public IntPtr WaitForMainWindow(TimeSpan timeout, out string why)
    {
        var deadline = DateTime.UtcNow + timeout;
        IntPtr last = IntPtr.Zero;
        Native.RECT lastRect = default;
        var stableSince = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited) { why = $"the process exited with code {_process.ExitCode} before a window appeared"; return IntPtr.Zero; }
            var hwnd = MainWindow();
            if (hwnd != IntPtr.Zero)
            {
                Native.GetWindowRect(hwnd, out var rect);
                if (hwnd == last && rect.Left == lastRect.Left && rect.Top == lastRect.Top && rect.Right == lastRect.Right && rect.Bottom == lastRect.Bottom)
                {
                    if (DateTime.UtcNow - stableSince > TimeSpan.FromMilliseconds(600)) { why = ""; return hwnd; }
                }
                else { last = hwnd; lastRect = rect; stableSince = DateTime.UtcNow; }
            }
            Thread.Sleep(150);
        }
        why = $"no visible window of the process within {timeout.TotalSeconds:0} s" + (TopLevelSummary() is { Length: > 0 } s ? " (top-level windows seen: " + s + ")" : "");
        return IntPtr.Zero;
    }

    /// <summary>The largest visible, un-owned top-level window of the process that is at least 200x150.</summary>
    public IntPtr MainWindow()
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        foreach (var hwnd in Windows())
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) continue;
            Native.GetWindowRect(hwnd, out var r);
            long w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 200 || h < 150 || w * h <= bestArea) continue;
            best = hwnd;
            bestArea = w * h;
        }
        return best;
    }

    /// <summary>Visible top-level windows of the process other than the main one; a second window during a test is an unexpected dialog.</summary>
    public List<string> OtherWindows(IntPtr main) =>
        Windows().Where(h => h != main && Native.IsWindowVisible(h)).Select(Describe).Where(d => !d.Contains("PopupWindowSiteBridge") && !d.Contains("class=\"IME\"") && !d.Contains("class=\"MSCTFIME UI\"") && !d.Contains("class=\"CiceroUIWndFrame\"")).ToList();

    /// <summary>
    /// Looks at five points inside the window's frame and asks Windows which top-level window is really there. Returns a description of a window of ANOTHER process that covers the
    /// app (a screen capture would then photograph that window, not the app), or null when the app is on top.
    /// </summary>
    public string? Covering(IntPtr hwnd)
    {
        var f = Capture.FrameOf(hwnd);
        var points = new[] { (2, 2), (1, 1), (3, 1), (1, 3), (2, 1) }.Select(q => new System.Drawing.Point(f.Left + f.Width * q.Item1 / 4, f.Top + f.Height * q.Item2 / 4)).ToArray();
        foreach (var point in points)
        {
            var root = Native.GetAncestor(Native.WindowFromPoint(point), 2);
            if (root == IntPtr.Zero) continue;
            Native.GetWindowThreadProcessId(root, out var owner);
            if (owner == (uint)_process.Id) continue;
            var name = "?";
            try { name = Process.GetProcessById((int)owner).ProcessName; } catch (ArgumentException) { }
            return $"process {name} (pid {owner}), {Describe(root)} at screen point {point.X},{point.Y}";
        }
        return null;
    }

    /// <summary>Brings the window forward and waits up to <paramref name="seconds"/> for it to be uncovered; returns the description of what still covers it, or null.</summary>
    public string? WaitUncovered(IntPtr hwnd, int seconds = 6)
    {
        string? cover = null;
        for (var i = 0; i < seconds * 2 + 1; i++)
        {
            BringToFront(hwnd);
            Thread.Sleep(250);
            cover = Covering(hwnd);
            if (cover is null) return null;
            Thread.Sleep(250);
        }
        return cover;
    }

    private string TopLevelSummary() => string.Join("; ", Windows().Select(Describe));

    private static string Describe(IntPtr hwnd)
    {
        var title = new StringBuilder(200);
        var cls = new StringBuilder(200);
        Native.GetWindowText(hwnd, title, title.Capacity);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        Native.GetWindowRect(hwnd, out var r);
        return $"class=\"{cls}\" title=\"{title}\" {(Native.IsWindowVisible(hwnd) ? "visible" : "hidden")} {r.Right - r.Left}x{r.Bottom - r.Top}";
    }

    private List<IntPtr> Windows()
    {
        var found = new List<IntPtr>();
        var pid = (uint)_process.Id;
        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid) found.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Makes the window the top, restored, foreground window with the mouse out of the way, so the screen capture shows it unobscured.</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        Native.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);   // topmost: no size, no move, show
        Native.SetForegroundWindow(hwnd);
        Native.SetCursorPos(Native.GetSystemMetrics(Native.SM_CXSCREEN) - 2, Native.GetSystemMetrics(Native.SM_CYSCREEN) - 2);
    }

    public void Stop()
    {
        try
        {
            if (!_process.HasExited) { _process.Kill(true); _process.WaitForExit(8000); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        Stop();
        _process.Dispose();
    }
}
