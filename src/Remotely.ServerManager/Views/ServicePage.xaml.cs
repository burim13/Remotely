using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.ServerManager;

namespace Remotely.ServerManager.Views;

public sealed partial class ServicePage : Page
{
    private readonly InfoBar _bar = new() { IsOpen = false, IsClosable = true };
    private readonly Border _dotHost = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _state = Ui.Text("", "SubtitleTextBlockStyle");
    private readonly TextBlock _stateDetail = Ui.Secondary("");
    private readonly ProgressRing _busy = new() { IsActive = false, Width = 24, Height = 24 };
    private readonly Button _start;
    private readonly Button _stop;
    private readonly Button _restart;
    private readonly Button _install;
    private readonly Button _uninstall;
    private bool _working;

    public ServicePage()
    {
        InitializeComponent();

        _start = Ui.SmallButton(Ui.T("Service_Start"), async (_, _) => await RunAsync(ServiceVerb.Start, Ui.T("Service_DoneStart")), accent: true);
        _stop = Ui.SmallButton(Ui.T("Service_Stop"), async (_, _) => await RunAsync(ServiceVerb.Stop, Ui.T("Service_DoneStop")));
        _restart = Ui.SmallButton(Ui.T("Service_Restart"), async (_, _) => await RunAsync(ServiceVerb.Restart, Ui.T("Service_DoneRestart")));
        _install = Ui.SmallButton(Ui.T("Service_Install"), async (_, _) => await RunAsync(ServiceVerb.Install, Ui.T("Service_DoneInstall")));
        _uninstall = Ui.SmallButton(Ui.T("Service_Uninstall"), async (_, _) => await UninstallAsync());

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        head.Children.Add(_dotHost);
        var texts = new StackPanel { Spacing = 2 };
        texts.Children.Add(_state);
        texts.Children.Add(_stateDetail);
        head.Children.Add(texts);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_start);
        buttons.Children.Add(_stop);
        buttons.Children.Add(_restart);
        buttons.Children.Add(_install);
        buttons.Children.Add(_uninstall);
        buttons.Children.Add(_busy);

        var card = new StackPanel { Spacing = 14 };
        card.Children.Add(head);
        card.Children.Add(buttons);

        var why = new StackPanel { Spacing = 6 };
        why.Children.Add(Ui.Text(Ui.T("Service_UacTitle"), "BodyStrongTextBlockStyle"));
        why.Children.Add(Ui.Secondary(Ui.T("Service_UacBody")));
        why.Children.Add(Ui.Secondary(Ui.T("Service_KeepsData")));

        Root.Children.Add(Ui.Text(Ui.T("Nav_Service"), "TitleTextBlockStyle"));
        Root.Children.Add(_bar);
        Root.Children.Add(Ui.Card(card));
        Root.Children.Add(Ui.Card(why));

        SetDot("DotNeutralStyle");
        SetButtons(ServiceState.Unknown);
        Loaded += OnLoaded;
        Unloaded += (_, _) => App.SnapshotChanged -= OnSnapshot;
    }

    private void SetDot(string styleKey)
    {
        var dot = Ui.Dot(styleKey);
        dot.Width = 16;
        dot.Height = 16;
        _dotHost.Child = dot;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.SnapshotChanged -= OnSnapshot;
        App.SnapshotChanged += OnSnapshot;
        if (App.Monitor.Latest is { } latest) Apply(latest);
        await App.PollAsync();
    }

    private void OnSnapshot(MonitorSnapshot snapshot) => Apply(snapshot);

    private void Apply(MonitorSnapshot snapshot)
    {
        _state.Text = Ui.T("Service_" + snapshot.Service);
        _stateDetail.Text = snapshot.Reachable ? Ui.T("Service_Answers") : Ui.T("Service_NoAnswer");
        SetDot(snapshot.Service switch
        {
            ServiceState.Running => "DotGoodStyle",
            ServiceState.Stopped => "DotBadStyle",
            ServiceState.StartPending or ServiceState.StopPending => "DotWarnStyle",
            _ => "DotNeutralStyle",
        });
        SetButtons(snapshot.Service);
    }

    private void SetButtons(ServiceState state)
    {
        var free = !_working;
        // Unknown means sc.exe could not tell us; leave every action available rather than lock the person out.
        _start.IsEnabled = free && state is ServiceState.Stopped or ServiceState.Unknown;
        _stop.IsEnabled = free && state is ServiceState.Running or ServiceState.StartPending or ServiceState.Unknown;
        _restart.IsEnabled = free && state is ServiceState.Running or ServiceState.Unknown;
        _install.IsEnabled = free && state is ServiceState.NotInstalled or ServiceState.Unknown;
        _uninstall.IsEnabled = free && state is not ServiceState.NotInstalled;
    }

    private async Task UninstallAsync()
    {
        if (!await Ui.ConfirmAsync(XamlRoot, Ui.T("Service_UninstallTitle"), Ui.T("Service_UninstallBody"), Ui.T("Service_Uninstall"))) return;
        await RunAsync(ServiceVerb.Uninstall, Ui.T("Service_DoneUninstall"));
    }

    private async Task RunAsync(ServiceVerb verb, string doneMessage)
    {
        _working = true;
        _busy.IsActive = true;
        SetButtons(App.Monitor.Latest?.Service ?? ServiceState.Unknown);
        _bar.IsOpen = false;
        try
        {
            var result = await ElevatedHelper.RunAsync(verb);
            if (result.Cancelled) Ui.Show(_bar, InfoBarSeverity.Warning, Ui.T("Service_Cancelled"));
            else if (result.Ok) Ui.Show(_bar, InfoBarSeverity.Success, doneMessage);
            else Ui.Show(_bar, InfoBarSeverity.Error, Ui.T("Service_Failed", result.ExitCode) + (result.Log.Length > 0 ? "\n" + result.Log : ""));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Ui.Show(_bar, InfoBarSeverity.Error, Ui.Describe(ex));
        }
        finally
        {
            _working = false;
            _busy.IsActive = false;
        }
        await App.PollAsync();
        SetButtons(App.Monitor.Latest?.Service ?? ServiceState.Unknown);
    }
}
