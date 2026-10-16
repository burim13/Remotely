using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.NetworkSetup;
using static Remotely.ServerManager.Setup.SetupUi;

namespace Remotely.ServerManager.Setup;

public sealed partial class SetupWizardPage
{
    private static readonly TimeSpan RenewedValidity = TimeSpan.FromDays(3650);

    private StackPanel _certDetails = null!;
    private InfoBar _certBar = null!;
    private InfoBar _renewBar = null!;
    private InfoBar _importBar = null!;
    private PasswordBox _pfxPassword = null!;
    private TextBlock _pfxPath = null!;
    private Button _pfxInstall = null!;
    private string? _chosenPfx;
    private string? _currentPin;

    private UIElement BuildCertificateStep()
    {
        var panel = Column();
        panel.Children.Add(Heading(T("Setup_Cert_Heading")));
        panel.Children.Add(Body(T("Setup_Cert_Intro")));
        _certDetails = Column(6);
        panel.Children.Add(_certDetails);
        _certBar = Bar();
        panel.Children.Add(_certBar);

        panel.Children.Add(Label(T("Setup_Cert_RenewHeading")));
        panel.Children.Add(Body(T("Setup_Cert_RenewIntro")));
        _renewBar = Bar();
        panel.Children.Add(ActionButton(T("Setup_Cert_Renew"), RenewAsync, _renewBar));
        panel.Children.Add(_renewBar);

        panel.Children.Add(Label(T("Setup_Cert_ImportHeading")));
        panel.Children.Add(Body(T("Setup_Cert_ImportIntro")));
        _pfxPath = Body(T("Setup_Cert_NoFile"));
        _pfxPassword = new PasswordBox { Header = T("Setup_Cert_Password"), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_pfxPassword, T("Setup_Cert_Password"));
        _importBar = Bar();
        _pfxInstall = ActionButton(T("Setup_Cert_Install"), InstallPfxAsync, _importBar);
        _pfxInstall.IsEnabled = false;
        panel.Children.Add(ActionButton(T("Setup_Cert_Choose"), ChoosePfxAsync, _importBar));
        panel.Children.Add(_pfxPath);
        panel.Children.Add(_pfxPassword);
        panel.Children.Add(ActionButton(T("Setup_Cert_Check"), CheckPfxAsync, _importBar));
        panel.Children.Add(_pfxInstall);
        panel.Children.Add(_importBar);
        return panel;
    }

    private async Task RefreshCertificateStepAsync()
    {
        _certDetails.Children.Clear();
        var report = await _wizard!.GetCertificateAsync();
        _currentPin = report.PinHex;
        if (report.Presented is null)
        {
            Show(_certBar, InfoBarSeverity.Informational, _wizard.TlsEnabled ? T("Setup_Cert_NotRead") : T("Setup_Cert_TlsOff"), _wizard.TlsEnabled ? T("Setup_Cert_NotReadBody") : null);
            return;
        }
        var c = report.Presented;
        _certDetails.Children.Add(ValueLine(T("Setup_Cert_Pin"), c.PinHex));
        _certDetails.Children.Add(Body(T("Setup_Cert_PinHelp")));
        _certDetails.Children.Add(ValueLine(T("Setup_Cert_Fingerprint"), c.FingerprintSha256));
        _certDetails.Children.Add(Body(T("Setup_Cert_FingerprintHelp")));
        _certDetails.Children.Add(ValueLine(T("Setup_Cert_Valid"), F("Setup_Cert_ValidRange", c.NotBefore.ToLocalTime().ToString("d", CultureInfo.CurrentCulture), c.NotAfter.ToLocalTime().ToString("d", CultureInfo.CurrentCulture))));
        _certDetails.Children.Add(ValueLine(T("Setup_Cert_Kind"), c.IsSelfSigned ? T("Setup_Cert_SelfSigned") : T("Setup_Cert_Issued")));
        switch (report.Expiry)
        {
            case ExpiryState.Ok: Show(_certBar, InfoBarSeverity.Success, T("Setup_Cert_Ok"), null); break;
            case ExpiryState.RenewSoon: Show(_certBar, InfoBarSeverity.Warning, T("Setup_Cert_Soon"), T("Setup_Cert_SoonBody")); break;
            case ExpiryState.Expired: Show(_certBar, InfoBarSeverity.Error, T("Setup_Cert_Expired"), T("Setup_Cert_ExpiredBody")); break;
            default: Show(_certBar, InfoBarSeverity.Warning, T("Setup_Cert_NotYet"), null); break;
        }
    }

    private async Task RenewAsync()
    {
        Show(_renewBar, InfoBarSeverity.Informational, T("Setup_Firewall_Asking"), T("Setup_Firewall_AskingBody"));
        var result = await _deps!.Services.Elevated.RenewCertificateAsync(RenewedValidity);
        if (result.Ok) Show(_renewBar, InfoBarSeverity.Success, T("Setup_Cert_Renewed"), T("Setup_Cert_RenewedBody"));
        else Show(_renewBar, InfoBarSeverity.Error, T("Setup_Cert_RenewFailed"), T(result.ErrorKey ?? "Setup_Err_Unexpected") + (string.IsNullOrEmpty(result.Detail) ? "" : " " + result.Detail));
    }

    private async Task ChoosePfxAsync()
    {
        _chosenPfx = await _deps!.PickPfxFileAsync();
        _pfxInstall.IsEnabled = false;
        _pfxPath.Text = _chosenPfx ?? T("Setup_Cert_NoFile");
        _importBar.IsOpen = false;
    }

    private async Task<PfxValidation?> ValidateChosenAsync()
    {
        if (_chosenPfx is null) { Show(_importBar, InfoBarSeverity.Warning, T("Setup_Cert_NoFile"), T("Setup_Cert_ChooseFirst")); return null; }
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(_chosenPfx); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Show(_importBar, InfoBarSeverity.Error, T("Setup_Pfx_CannotRead"), ex.Message);
            return null;
        }
        return PfxValidator.Validate(bytes, _pfxPassword.Password, DateTimeOffset.UtcNow);
    }

    private async Task CheckPfxAsync()
    {
        _pfxInstall.IsEnabled = false;
        var v = await ValidateChosenAsync();
        if (v is null) return;
        if (!v.IsValid)
        {
            Show(_importBar, InfoBarSeverity.Error, T(v.ErrorKey!), T(v.ErrorKey + "_Help"));
            return;
        }
        var s = v.Summary!;
        var pinChanges = _currentPin is not null && !string.Equals(_currentPin, s.PinHex, StringComparison.OrdinalIgnoreCase);
        var body = F("Setup_Cert_ImportOk", s.Subject, s.NotAfter.ToLocalTime().ToString("d", CultureInfo.CurrentCulture), s.PinHex, s.FingerprintSha256)
            + (pinChanges ? Environment.NewLine + T("Setup_Cert_PinChanges") : "")
            + string.Concat(v.WarningKeys.Select(k => Environment.NewLine + T(k)));
        Show(_importBar, pinChanges || v.WarningKeys.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success, T("Setup_Cert_ImportValid"), body);
        _pfxInstall.IsEnabled = true;
    }

    private async Task InstallPfxAsync()
    {
        var v = await ValidateChosenAsync();
        if (v is not { IsValid: true } || _chosenPfx is null) { _pfxInstall.IsEnabled = false; return; }
        Show(_importBar, InfoBarSeverity.Informational, T("Setup_Firewall_Asking"), T("Setup_Firewall_AskingBody"));
        var result = await _deps!.Services.Elevated.InstallCertificateAsync(_chosenPfx, _pfxPassword.Password);
        if (result.Ok) Show(_importBar, InfoBarSeverity.Success, T("Setup_Cert_Installed"), T("Setup_Cert_InstalledBody"));
        else Show(_importBar, InfoBarSeverity.Error, T("Setup_Cert_InstallFailed"), T(result.ErrorKey ?? "Setup_Err_Unexpected") + (string.IsNullOrEmpty(result.Detail) ? "" : " " + result.Detail));
        _pfxPassword.Password = "";
    }
}
