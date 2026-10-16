using Microsoft.UI.Xaml;
using Remotely.UI.Localization;

namespace Remotely.ServerManager;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = Loc.Get("App_Name");
        Info.Text = "Skeleton build. The management screens arrive with the Server Manager work.";
    }
}
