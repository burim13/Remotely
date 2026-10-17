using Microsoft.UI.Xaml.Controls;

namespace Remotely.App.Views.Connect;

/// <summary>The Connect screen: the <see cref="ConnectCard"/> on its own page. The card's failure button opens the connection check.</summary>
public sealed partial class ConnectPage : Page
{
    public ConnectPage()
    {
        InitializeComponent();
        Card.DiagnoseRequested += (address, label) => Frame?.Navigate(typeof(DiagnosisPage), new DiagnosisRequest(address, label));
    }
}
