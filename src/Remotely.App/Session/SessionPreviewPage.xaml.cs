using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Remotely.UI.Localization;
using Remotely.ViewerCore;
using Remotely.ViewerCore.Mapping;

namespace Remotely.App;

/// <summary>A session screen without a session: the real toolbar, remote view and overlays over a still picture (the app icon). Started by CI with <c>--show SessionPreviewPage</c>.</summary>
public sealed partial class SessionPreviewPage : Page
{
    public SessionPreviewPage()
    {
        InitializeComponent();
        Toolbar.Configure("Preview", canControl: true, secureAttention: true, clipboard: true, files: true);
        Toolbar.SetStats(true);
        Toolbar.ScaleChosen += mode => { Screen.SetMode(mode); Toolbar.SetScale(mode); };
        Screen.SetFrame(new BitmapImage(new Uri("ms-appx:///Assets/remotely-256.png")), 256, 256);
        Screen.CanControl = false;
        StatsText.Text = SessionStatsText.Format(new StatsSnapshot(0, 0, 0, 0, null, null, "JPEG"), null, 256, 256);
        KeyboardText.Text = Loc.Get("Session_Badge_Remote");
        KeyboardDot.Fill = Palette.Connected(this);
    }
}
