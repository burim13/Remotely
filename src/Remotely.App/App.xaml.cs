using Microsoft.UI.Xaml;

namespace Remotely.App;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    public static ShareService Share { get; } = new();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs();
        var at = Array.IndexOf(argv, "--selftest");
        if (at >= 0 && at + 1 < argv.Length)
        {
            Environment.Exit(await SelfTest.RunAsync(argv[at + 1]));
            return;
        }
        _window = new MainWindow();
        _window.Activate();
    }
}
