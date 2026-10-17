using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Remotely.UI.Localization;
using Remotely.ViewerCore.Mapping;

namespace Remotely.App;

/// <summary>
/// The floating toolbar of a session. It only raises events and shows state; <see cref="SessionWindow"/> decides what they do,
/// and the preview page shows it on its own for CI screenshots. The window hides it when the pointer has been away for a while.
/// </summary>
public sealed partial class SessionToolbar : UserControl
{
    private int _openMenus;
    private bool _suppress;
    private bool _connected = true, _canControl = true, _secureAttention, _clipboard, _files;

    public SessionToolbar()
    {
        InitializeComponent();
        Describe(MonitorButton, "Session_Monitor");
        Describe(ScaleButton, "Session_Scale");
        Describe(FullScreenButton, "Session_FullScreen");
        Describe(StatsButton, "Session_Stats");
        Describe(KeyboardButton, "Session_KeyboardCapture");
        Describe(ViewOnlyButton, "Session_ViewOnly");
        Describe(SasButton, "Session_Sas");
        Describe(ClipboardButton, "Session_Clipboard");
        Describe(FilesButton, "Session_Files");
        Describe(PinButton, "Session_PinToolbar");
        Describe(EndButton, "Session_End");
        FitItem.Text = Loc.Get("Session_ScaleFit");
        ActualItem.Text = Loc.Get("Session_ScaleActual");
        FillItem.Text = Loc.Get("Session_ScaleFill");
        FitItem.IsChecked = true;
        KeyboardButton.IsChecked = true;
        SetMonitors([MonitorEntry.All], MonitorEntry.All);
        foreach (FlyoutBase flyout in new FlyoutBase[] { MonitorMenu, ScaleMenu })
        {
            flyout.Opened += (_, _) => { _openMenus++; MenuStateChanged?.Invoke(); };
            flyout.Closed += (_, _) => { _openMenus = Math.Max(0, _openMenus - 1); MenuStateChanged?.Invoke(); };
        }
    }

    private static void Describe(ICommandBarElement element, string key)
    {
        var text = Loc.Get(key);
        switch (element)
        {
            case AppBarButton b: b.Label = text; ToolTipService.SetToolTip(b, text); AutomationProperties.SetName(b, text); break;
            case AppBarToggleButton t: t.Label = text; ToolTipService.SetToolTip(t, text); AutomationProperties.SetName(t, text); break;
        }
    }

    public event Action<MonitorEntry>? MonitorChosen;
    public event Action<ScaleMode>? ScaleChosen;
    public event Action<bool>? FilesChanged, FullScreenChanged, StatsChanged, KeyboardCaptureChanged, ViewOnlyChanged, ClipboardChanged, PinnedChanged;
    public event Action? SecureAttentionClicked, EndClicked;
    /// <summary>Pointer entered or left the bar, or a menu of the bar opened or closed.</summary>
    public event Action? MenuStateChanged, PointerStateChanged;

    public bool IsPointerInside { get; private set; }
    public bool IsMenuOpen => _openMenus > 0;
    public bool Pinned => PinButton.IsChecked == true;

    public void Configure(string hostName, bool canControl, bool secureAttention, bool clipboard, bool files)
    {
        HostLabel.Text = hostName;
        (_canControl, _secureAttention, _clipboard, _files) = (canControl, secureAttention, clipboard, files);
        ApplyEnabled();
    }

    private void ApplyEnabled()
    {
        ViewOnlyButton.IsEnabled = _connected && _canControl;
        KeyboardButton.IsEnabled = _connected && _canControl;
        SasButton.IsEnabled = _connected && _canControl && _secureAttention;
        ClipboardButton.IsEnabled = _connected && _clipboard;
        FilesButton.IsEnabled = _connected && _files;
        MonitorButton.IsEnabled = ScaleButton.IsEnabled = FullScreenButton.IsEnabled = StatsButton.IsEnabled = _connected;
    }

    public void SetFiles(bool on) => FilesButton.IsChecked = on;
    public void SetClipboard(bool on) => ClipboardButton.IsChecked = on;
    public void SetKeyboardCapture(bool on) => KeyboardButton.IsChecked = on;
    public void SetViewOnly(bool on) => ViewOnlyButton.IsChecked = on;
    public void SetFullScreen(bool on) => FullScreenButton.IsChecked = on;
    public void SetStats(bool on) => StatsButton.IsChecked = on;
    public void SetPinned(bool on) => PinButton.IsChecked = on;

    /// <summary>Everything that needs a live session is switched off while the connection is down.</summary>
    public void SetConnected(bool connected)
    {
        _connected = connected;
        ApplyEnabled();
    }

    public void SetScale(ScaleMode mode)
    {
        _suppress = true;
        FitItem.IsChecked = mode == ScaleMode.Fit;
        ActualItem.IsChecked = mode == ScaleMode.Actual;
        FillItem.IsChecked = mode == ScaleMode.Fill;
        _suppress = false;
    }

    public void SetMonitors(IReadOnlyList<MonitorEntry> monitors, MonitorEntry selected)
    {
        MonitorMenu.Items.Clear();
        foreach (var monitor in monitors)
        {
            var item = new RadioMenuFlyoutItem
            {
                GroupName = "SessionMonitor",
                Text = MonitorName(monitor),
                IsChecked = monitor.Index == selected.Index,
                Tag = monitor,
            };
            item.Click += (_, _) => { if (item.Tag is MonitorEntry chosen) MonitorChosen?.Invoke(chosen); };
            MonitorMenu.Items.Add(item);
        }
    }

    public static string MonitorName(MonitorEntry monitor) =>
        monitor.IsAll ? Loc.Get("Session_AllMonitors")
        : string.IsNullOrEmpty(monitor.Name) ? string.Format(Loc.Get("Session_MonitorN"), monitor.Index + 1)
        : monitor.Name;

    private void OnBarPointerEntered(object sender, PointerRoutedEventArgs e) { IsPointerInside = true; PointerStateChanged?.Invoke(); }
    private void OnBarPointerExited(object sender, PointerRoutedEventArgs e) { IsPointerInside = false; PointerStateChanged?.Invoke(); }

    private void OnScaleItem(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        ScaleChosen?.Invoke(sender == ActualItem ? ScaleMode.Actual : sender == FillItem ? ScaleMode.Fill : ScaleMode.Fit);
    }

    private void OnFullScreen(object sender, RoutedEventArgs e) => FullScreenChanged?.Invoke(FullScreenButton.IsChecked == true);
    private void OnStats(object sender, RoutedEventArgs e) => StatsChanged?.Invoke(StatsButton.IsChecked == true);
    private void OnKeyboard(object sender, RoutedEventArgs e) => KeyboardCaptureChanged?.Invoke(KeyboardButton.IsChecked == true);
    private void OnViewOnly(object sender, RoutedEventArgs e) => ViewOnlyChanged?.Invoke(ViewOnlyButton.IsChecked == true);
    private void OnClipboard(object sender, RoutedEventArgs e) => ClipboardChanged?.Invoke(ClipboardButton.IsChecked == true);
    private void OnPin(object sender, RoutedEventArgs e) => PinnedChanged?.Invoke(PinButton.IsChecked == true);
    private void OnSas(object sender, RoutedEventArgs e) => SecureAttentionClicked?.Invoke();
    private void OnFiles(object sender, RoutedEventArgs e) => FilesChanged?.Invoke(FilesButton.IsChecked == true);
    private void OnEnd(object sender, RoutedEventArgs e) => EndClicked?.Invoke();
}
