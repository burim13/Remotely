using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.NetworkSetup;
using static Remotely.ServerManager.Setup.SetupUi;

namespace Remotely.ServerManager.Setup;

public sealed partial class SetupWizardPage
{
    private RadioButtons _addressChoice = null!;
    private TextBox _publicName = null!;
    private NumberBox _portBox = null!;
    private InfoBar _addressBar = null!;
    private TextBlock _addressIntro = null!;
    private IReadOnlyList<LocalAddress> _candidates = [];

    private UIElement BuildAddressStep()
    {
        var panel = Column();
        panel.Children.Add(Heading(T("Setup_Address_Heading")));
        _addressIntro = Body(T("Setup_Address_Intro"));
        panel.Children.Add(_addressIntro);

        _addressChoice = new RadioButtons { Header = T("Setup_Address_LocalHeader") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_addressChoice, T("Setup_Address_LocalHeader"));
        panel.Children.Add(_addressChoice);

        _publicName = new TextBox { Header = T("Setup_Address_NameHeader"), PlaceholderText = T("Setup_Address_NamePlaceholder"), MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_publicName, T("Setup_Address_NameHeader"));
        panel.Children.Add(_publicName);
        panel.Children.Add(Body(T("Setup_Address_NameHelp")));

        _portBox = new NumberBox
        {
            Header = T("Setup_Address_PortHeader"), Minimum = 1, Maximum = 65535, Value = 8443,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MaxWidth = 220, HorizontalAlignment = HorizontalAlignment.Left,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_portBox, T("Setup_Address_PortHeader"));
        panel.Children.Add(_portBox);
        panel.Children.Add(Body(T("Setup_Address_PortHelp")));

        _addressBar = Bar();
        panel.Children.Add(_addressBar);
        return panel;
    }

    private async Task LoadAddressStepAsync()
    {
        var wizard = _wizard!;
        var snapshot = await wizard.LoadServerAsync();
        if (snapshot is null) Show(_addressBar, InfoBarSeverity.Informational, T("Setup_Address_NoServerTitle"), T("Setup_Address_NoServerBody"));
        else _addressBar.IsOpen = false;

        _portBox.Value = wizard.Port;
        if (_publicName.Text.Length == 0) _publicName.Text = wizard.PublicName;

        var detected = wizard.DetectAddresses();
        _candidates = detected.Candidates;
        _addressChoice.Items.Clear();
        foreach (var c in _candidates)
            _addressChoice.Items.Add(F("Setup_Address_Item", c.Address.ToString(), c.AdapterName));
        if (_candidates.Count == 0)
        {
            Show(_addressBar, InfoBarSeverity.Warning, T("Setup_Address_NoneTitle"), T("Setup_Address_NoneBody"));
            return;
        }
        var chosen = wizard.ChosenLocalAddress;
        var index = _candidates.ToList().FindIndex(c => c.Address.Equals(chosen));
        _addressChoice.SelectedIndex = index < 0 ? 0 : index;
    }

    private async Task<bool> ApplyAddressStepAsync()
    {
        var wizard = _wizard!;
        var name = wizard.SetPublicName(_publicName.Text);
        if (!name.IsValid)
        {
            Show(_addressBar, InfoBarSeverity.Error, T("Setup_Err_NameTitle"), T(name.ErrorKey!));
            return false;
        }
        var port = double.IsNaN(_portBox.Value) ? 0 : (int)_portBox.Value;
        if (name.Port is not null) port = name.Port.Value;   // "name:port" typed in the name box wins
        if (!wizard.TrySetPort(port))
        {
            Show(_addressBar, InfoBarSeverity.Error, T("Setup_Err_PortTitle"), T("Setup_Err_PortInvalid"));
            return false;
        }
        _portBox.Value = wizard.Port;
        _publicName.Text = wizard.PublicName;
        if (_candidates.Count > 0 && _addressChoice.SelectedIndex >= 0 && _addressChoice.SelectedIndex < _candidates.Count)
            wizard.ChooseLocalAddress(_candidates[_addressChoice.SelectedIndex].Address);

        var saved = await wizard.ApplyToServerAsync();
        if (saved) Show(_addressBar, InfoBarSeverity.Success, T("Setup_Address_SavedTitle"), T("Setup_Address_SavedBody"));
        else Show(_addressBar, InfoBarSeverity.Warning, T("Setup_Address_NotSavedTitle"), T("Setup_Address_NotSavedBody"));
        return true;
    }
}
