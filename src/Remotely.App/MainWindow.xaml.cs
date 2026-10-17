using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Remotely.App.Shell;
using Remotely.App.Views.Onboarding;
using Remotely.UI.Localization;
using Windows.Graphics;
using Launcher = Windows.System.Launcher;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace Remotely.App;

public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1100, DefaultHeight = 720, MinWidth = 640, MinHeight = 480;
    private const string HelpUrl = "https://github.com/burim13/Remotely#readme";

    private readonly IReadOnlyList<NavEntry> _entries;
    private readonly NavEntry? _settingsEntry;
    private readonly DispatcherQueueTimer _toastTimer;
    private WindowBounds? _lastNormal;
    private bool _maximizeOnShow;
    private bool _quitting;

    public MainWindow()
    {
        InitializeComponent();
        Title = Loc.Get("App_Name");
        TitleText.Text = Title;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "remotely.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        TryUseMica();
        ApplyPlacement();

        var all = NavigationCatalog.All(typeof(MainWindow).Assembly);
        _entries = NavigationCatalog.Menu(all);
        _settingsEntry = all.FirstOrDefault(e => e.Tag == NavigationCatalog.SettingsTag);
        foreach (var entry in _entries)
        {
            var text = Loc.Get(entry.TitleKey);
            var item = new NavigationViewItem { Content = text, Tag = entry.Tag, Icon = new FontIcon { Glyph = char.ConvertFromUtf32(entry.Glyph) } };
            AutomationProperties.SetName(item, text);
            Nav.MenuItems.Add(item);
        }
        AddShortcuts();

        _toastTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(6);
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBar.IsOpen = false; };

        AppWindow.Changed += (_, args) => { if (args.DidSizeChange || args.DidPositionChange) RememberBounds(); };
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        Root.Loaded += (_, _) => IsReady = true;
        App.ApplyTheme(this);
        if (App.Options.Show is null && !App.Options.IsSelfTest) Remotely.App.Update.AppUpdates.StartBackgroundCheck();   // does nothing unless the person turned update checks on
        if (App.Options.Show is null && !App.Options.IsSelfTest) Views.Unattended.UnattendedController.Instance.EnsureBackground(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());   // answers consent questions from the service even with the page closed

        ShowVerifier.Attach(Pages);
        if (NeedsOnboarding) StartOnboarding();
        else
        {
            Nav.SelectedItem = Nav.MenuItems[0];   // raises SelectionChanged, which shows the first page
            if (ShowVerifier.Active) ShowVerifier.Navigate(Pages, DispatcherQueue);
        }
    }

    /// <summary>True once the first page is on screen; the unhandled-exception dialog only takes over after this.</summary>
    internal bool IsReady { get; private set; }

    /// <summary>First run: nothing set up, not a test or invite launch, not started minimized at sign-in.</summary>
    internal static bool NeedsOnboarding =>
        !App.Settings.OnboardingDone && !App.Settings.IsConfigured
        && App.Options.Show is null && !App.Options.IsSelfTest && !App.Options.HasWork && !App.Options.Minimized;

    private bool OnboardingActive => OnboardingFrame.Visibility == Visibility.Visible;

    // ---- showing, hiding, quitting ----

    /// <summary>First time on screen. Maximizing happens here because the presenter state is not reliable before the window is shown.</summary>
    internal void ShowInitially()
    {
        Activate();
        if (_maximizeOnShow && AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
    }

    /// <summary>Brings the window back from the tray or the taskbar and to the front (the second launch gave this process the right to do so).</summary>
    internal void ShowAndFocus()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    internal void Quit()
    {
        _quitting = true;
        Close();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SaveBounds();
        if (_quitting || !App.Settings.CloseToTray || App.Tray is not { IsAdded: true }) return;
        args.Cancel = true;
        sender.Hide();
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        try { await App.Share.DisposeAsync(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { App.LogCrash(ex); }
        App.ReleaseShell();
        if (_quitting) Application.Current.Exit();
    }

    // ---- window size and position ----

    private void ApplyPlacement()
    {
        try
        {
            var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            if (scale < 1) scale = 1;
            var areas = new List<PixelRect> { ToRect(DisplayArea.Primary.WorkArea) };
            var all = DisplayArea.FindAll();
            for (var i = 0; i < all.Count; i++)   // index, do not foreach: enumerating this projected list throws InvalidCastException (seen on CI)
            {
                var r = ToRect(all[i].WorkArea);
                if (!areas.Contains(r)) areas.Add(r);
            }
            var s = App.Settings;
            WindowBounds? saved = s.WindowX is { } x && s.WindowY is { } y && s.WindowWidth is { } w && s.WindowHeight is { } h
                ? new WindowBounds(x, y, w, h, s.WindowMaximized) : null;
            var fitted = WindowPlacement.Fit(saved, areas, (int)(DefaultWidth * scale), (int)(DefaultHeight * scale), (int)(MinWidth * scale), (int)(MinHeight * scale));
            AppWindow.MoveAndResize(new RectInt32(fitted.X, fitted.Y, fitted.Width, fitted.Height));
            _lastNormal = fitted with { Maximized = false };
            _maximizeOnShow = fitted.Maximized;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            App.LogCrash(ex);   // the window then opens where Windows puts it
        }
    }

    private static PixelRect ToRect(RectInt32 r) => new(r.X, r.Y, r.Width, r.Height);

    private void RememberBounds()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            _lastNormal = new WindowBounds(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height, false);
    }

    private void SaveBounds()
    {
        if (_lastNormal is not { } b) return;
        var s = App.Settings;
        (s.WindowX, s.WindowY, s.WindowWidth, s.WindowHeight) = (b.X, b.Y, b.Width, b.Height);
        s.WindowMaximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        try { s.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.LogCrash(ex); }
    }

    private void TryUseMica()
    {
        try
        {
            if (!Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()) return;   // solid background stays
            SystemBackdrop = new MicaBackdrop();
            Root.Background = new SolidColorBrush(Colors.Transparent);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException) { App.LogCrash(ex); }
    }

    // ---- navigation ----

    /// <summary>Shows a page by its navigation tag ("home", "computers", "settings", or any tag a page registered).</summary>
    internal void Show(string page)
    {
        if (OnboardingActive) return;
        if (page == NavigationCatalog.SettingsTag) Nav.SelectedItem = Nav.SettingsItem;
        else if (Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == page) is { } item) Nav.SelectedItem = item;
    }

    private void OnNavSelected(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var entry = args.IsSettingsSelected ? _settingsEntry : _entries.FirstOrDefault(e => e.Tag == (args.SelectedItemContainer?.Tag as string));
        if (entry is not null && Pages.CurrentSourcePageType != entry.PageType) Pages.Navigate(entry.PageType);
    }

    private void AddShortcuts()
    {
        for (var i = 0; i < Math.Min(9, _entries.Count); i++)
        {
            var tag = _entries[i].Tag;
            AddShortcut(VirtualKey.Number1 + i, VirtualKeyModifiers.Control, () => Show(tag));
        }
        AddShortcut((VirtualKey)188 /* VK_OEM_COMMA */, VirtualKeyModifiers.Control, () => Show(NavigationCatalog.SettingsTag));
        AddShortcut(VirtualKey.F1, VirtualKeyModifiers.None, () => _ = Launcher.LaunchUriAsync(new Uri(HelpUrl)));
    }

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) => { e.Handled = true; action(); };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    // ---- in-app notice ----

    /// <summary>Shows a short notice at the bottom of the window. False when the window is hidden (the caller then uses the tray balloon).</summary>
    internal bool TryShowToast(string title, string message)
    {
        if (!AppWindow.IsVisible) return false;
        ToastBar.Title = title;
        ToastBar.Message = message;
        ToastBar.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Start();
        return true;
    }

    // ---- first run ----

    private void StartOnboarding()
    {
        Nav.Visibility = Visibility.Collapsed;
        OnboardingFrame.Visibility = Visibility.Visible;
        OnboardingFrame.Navigate(typeof(OnboardingPage));
        if (OnboardingFrame.Content is OnboardingPage page) page.Finished += EndOnboarding;
    }

    private void EndOnboarding()
    {
        OnboardingFrame.Visibility = Visibility.Collapsed;
        OnboardingFrame.Content = null;
        Nav.Visibility = Visibility.Visible;
        Nav.SelectedItem = null;
        Show(App.Settings.IsConfigured ? "computers" : "home");
    }

    // ---- command-line work (start-up and forwarded from a second launch) ----

    /// <summary>--invite and --pair set up the server link (the installer uses this), --connect-token fills the token box.</summary>
    internal async Task HandleOptionsAsync(AppOptions options)
    {
        if (options.Invite is { Length: > 0 } invite)
        {
            var (ok, message) = await Join.WithInviteAsync(invite);
            App.LogInfo("invite: ok=" + ok + (ok ? "" : " " + message));
            if (!ok) { await App.Services.ShowErrorAsync(Loc.Get("Invite_Failed_Title"), message); return; }
            if (OnboardingActive) EndOnboarding();
            Show("computers");
        }
        else if (options.Pair is { Length: > 0 } code && App.Settings.IsConfigured)
        {
            var (ok, message) = await Join.PairAsync(code);
            if (!ok) { await App.Services.ShowErrorAsync(Loc.Get("Invite_Failed_Title"), message); return; }
            Show("computers");
        }
        if (options.ConnectToken is { Length: > 0 } token)
        {
            if (OnboardingActive) EndOnboarding();
            Show("home");
            if (Pages.Content is HomePage home) home.SetToken(token);
        }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
