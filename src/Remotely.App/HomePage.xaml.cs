using Microsoft.UI.Xaml.Controls;
using Remotely.App.Views.Connect;
using Remotely.Core.Lan;

namespace Remotely.App;

/// <summary>Same-network home: "Share this PC" on the left, "Connect to a PC" on the right. Both are self-contained cards.</summary>
public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        var addresses = LanNetwork.LocalAddresses();
        NetworkText.Text = addresses.Count == 0
            ? Remotely.UI.Localization.Loc.Get("Home_NoNetwork")
            : string.Format(Remotely.UI.Localization.Loc.Get("Home_ThisPc"), string.Join(", ", addresses));
        Connect.DiagnoseRequested += (address, label) => Frame.Navigate(typeof(DiagnosisPage), new DiagnosisRequest(address, label));
    }

    /// <summary>Fills the token box (for <c>--connect-token</c>).</summary>
    public void SetToken(string token) => Connect.SetToken(token);
}
