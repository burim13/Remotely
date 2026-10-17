using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Remotely.App.Services;
using Remotely.Core.Storage;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Settings;

/// <summary>Builds the control for one catalogued setting: label, help text, the control, a locked note when policy forces the value, and a one-line result after a change.</summary>
internal static class SettingRows
{
    public static UIElement Create(SettingDef def, SettingsSet set, PolicyOverrides policy)
    {
        var label = Loc.Get(def.LabelKey);
        var help = Loc.Get(def.HelpKey);
        var forced = def.ForcedBy?.Invoke(policy);
        var current = forced ?? (def.Id == "StartWithWindows" ? WindowsStartup.IsEnabled : def.Get(set));

        var status = SettingsUi.Secondary("");
        status.Visibility = Visibility.Collapsed;
        var loading = false;

        void Show(string key, params object[] args)
        {
            status.Text = args.Length == 0 ? Loc.Get(key) : SettingsUi.Format(key, args);
            status.Visibility = Visibility.Visible;
        }

        // Returns the value now in the setting, so the caller can put the control back when the change was refused.
        object Commit(object value)
        {
            if (!def.TryNormalize(value, out var normalized))
            {
                if (def.Kind == SettingKind.Number) Show("Settings_Status_InvalidNumber", def.Min, def.Max);
                else Show("Settings_Status_Invalid");
                return def.Get(set);
            }
            var outcome = SettingsHub.Apply(def, set, normalized);
            Show(outcome switch
            {
                ApplyOutcome.Saved => "Settings_Status_Saved",
                ApplyOutcome.NeedsRestart => "Settings_Status_Restart",
                ApplyOutcome.SaveFailed => "Settings_Status_SaveFailed",
                _ => "Settings_Status_Refused",
            });
            return def.Get(set);
        }

        FrameworkElement control;
        switch (def.Kind)
        {
            case SettingKind.Toggle:
            {
                var toggle = new ToggleSwitch { IsOn = (bool)current, OnContent = Loc.Get("Settings_On"), OffContent = Loc.Get("Settings_Off") };
                toggle.Toggled += (_, _) =>
                {
                    if (loading) return;
                    var now = (bool)Commit(toggle.IsOn);
                    if (now != toggle.IsOn) { loading = true; toggle.IsOn = now; loading = false; }
                };
                control = toggle;
                break;
            }
            case SettingKind.Choice:
            {
                var combo = new ComboBox { MinWidth = 240 };
                foreach (var choice in def.Choices) combo.Items.Add(new ComboBoxItem { Content = Loc.Get(choice.LabelKey), Tag = choice.Value });
                void Select(string value)
                {
                    foreach (var item in combo.Items.OfType<ComboBoxItem>()) if ((string?)item.Tag == value) combo.SelectedItem = item;
                }
                Select((string)current);
                combo.SelectionChanged += (_, _) =>
                {
                    if (loading || combo.SelectedItem is not ComboBoxItem { Tag: string value }) return;
                    var now = (string)Commit(value);
                    if (now != value) { loading = true; Select(now); loading = false; }
                };
                control = combo;
                break;
            }
            case SettingKind.Number:
            {
                var box = new NumberBox
                {
                    Minimum = def.Min, Maximum = def.Max, Value = Convert.ToDouble(current), MinWidth = 200, SmallChange = 1, LargeChange = 10,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                };
                box.ValueChanged += (_, args) =>
                {
                    if (loading) return;
                    if (double.IsNaN(args.NewValue)) { loading = true; box.Value = Convert.ToDouble(def.Get(set)); loading = false; Show("Settings_Status_InvalidNumber", def.Min, def.Max); return; }
                    var now = Convert.ToDouble(Commit((long)Math.Round(args.NewValue)));
                    if (now != box.Value) { loading = true; box.Value = now; loading = false; }
                };
                control = box;
                break;
            }
            case SettingKind.Folder:
            {
                var path = new TextBox { Text = (string)current, IsReadOnly = true, PlaceholderText = Loc.Get("Settings_Folder_Default"), MinWidth = 320 };
                AutomationProperties.SetName(path, label);
                var choose = SettingsUi.ButtonOf("Settings_Folder_Choose", async (_, _) =>
                {
                    if (await App.Services.PickFolderAsync() is not { Length: > 0 } folder) return;
                    path.Text = (string)Commit(folder);
                });
                var useDefault = SettingsUi.ButtonOf("Settings_Folder_UseDefault", (_, _) => path.Text = (string)Commit(""));
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(path);
                row.Children.Add(choose);
                row.Children.Add(useDefault);
                choose.IsEnabled = useDefault.IsEnabled = forced is null;
                control = row;
                break;
            }
            default:
            {
                var text = new TextBox { Text = (string)current, MaxLength = 260, MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
                text.LostFocus += (_, _) =>
                {
                    if (text.Text.Trim() == (string)def.Get(set)) return;
                    text.Text = (string)Commit(text.Text);
                };
                control = text;
                break;
            }
        }

        if (control is not StackPanel)
        {
            AutomationProperties.SetName(control, label);
            AutomationProperties.SetHelpText(control, help);
            control.IsEnabled = forced is null;
        }

        var children = new List<UIElement> { SettingsUi.Strong(label), SettingsUi.Secondary(help), control };
        if (forced is not null) children.Add(SettingsUi.Secondary(Loc.Get("Settings_PolicyManaged")));
        children.Add(status);
        return SettingsUi.CardOf([.. children]);
    }
}
