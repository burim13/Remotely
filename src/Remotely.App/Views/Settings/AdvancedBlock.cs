using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.Core.Storage;
using Remotely.UI.Localization;

namespace Remotely.App.Views.Settings;

/// <summary>Export, import, reset, the data folder, and what happened when the settings file was last read.</summary>
internal static class AdvancedBlock
{
    public static UIElement Create(PolicyOverrides policy)
    {
        var result = new InfoBar { IsOpen = false, IsClosable = true };

        void Say(InfoBarSeverity severity, string message)
        {
            result.Severity = severity;
            result.Message = message;
            result.IsOpen = true;
        }

        var export = SettingsUi.ButtonOf("Settings_Export", async (_, _) =>
        {
            if (await FileDialogs.PickSavePathAsync("remotely-settings") is not { } path) return;
            try
            {
                File.WriteAllText(path, SettingsTransfer.Export(SettingsHub.Load()));
                Say(InfoBarSeverity.Success, SettingsUi.Format("Settings_Export_Done", path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say(InfoBarSeverity.Error, Loc.Get("Settings_File_WriteFailed")); }
        });

        var import = SettingsUi.ButtonOf("Settings_Import", async (_, _) =>
        {
            if (await App.Services.PickFileAsync() is not { Length: > 0 } path) return;
            if (FileDialogs.ReadSmallText(path) is not { } text) { Say(InfoBarSeverity.Error, Loc.Get("Settings_File_ReadFailed")); return; }
            var set = SettingsHub.Load();
            SettingsImportResult outcome;
            try { outcome = SettingsTransfer.Import(text, set, policy); }
            catch (FormatException) { Say(InfoBarSeverity.Error, Loc.Get("Settings_Import_Bad")); return; }
            var saved = SettingsHub.PersistAll(set);
            var message = SettingsUi.Format("Settings_Import_Done", outcome.Applied.Count, outcome.Rejected.Count + outcome.Unknown.Count, outcome.LockedByPolicy.Count);
            Say(saved && outcome.Rejected.Count + outcome.Unknown.Count + outcome.LockedByPolicy.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning, message);
        });

        var reset = SettingsUi.ButtonOf("Settings_Reset", async (_, _) =>
        {
            if (!await App.Services.ConfirmAsync(Loc.Get("Settings_Reset_Title"), Loc.Get("Settings_Reset_Body"), Loc.Get("Settings_Reset_Accept"), Loc.Get("Settings_Reset_Cancel"))) return;
            var set = SettingsHub.Load();
            SettingsCatalog.ResetAll(set);
            var saved = SettingsHub.PersistAll(set);
            Say(saved ? InfoBarSeverity.Success : InfoBarSeverity.Warning, Loc.Get(saved ? "Settings_Reset_Done" : "Settings_Status_SaveFailed"));
        });

        var folder = SettingsUi.ButtonOf("Settings_OpenFolder", (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + ViewerSettings.Dir + "\"") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Say(InfoBarSeverity.Error, Loc.Get("Settings_OpenFolder_Failed")); }
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { export, import, reset, folder } };
        var items = new List<UIElement>
        {
            SettingsUi.Subtitle(Loc.Get("Settings_Advanced_Title")),
            SettingsUi.Secondary(Loc.Get("Settings_Advanced_Help")),
            buttons,
            result,
        };
        if (LoadNote() is { } note) items.Add(note);
        return SettingsUi.CardOf([.. items]);
    }

    /// <summary>A line about the way viewer.json was read at start-up when that was not the ordinary case.</summary>
    private static UIElement? LoadNote()
    {
        var info = ViewerSettings.LastLoad;
        return info.Status switch
        {
            LoadStatus.Damaged => SettingsUi.Bar(InfoBarSeverity.Warning, SettingsUi.Format("Settings_Load_Damaged", info.BackupPath ?? "")),
            LoadStatus.NewerThanThisVersion => SettingsUi.Bar(InfoBarSeverity.Warning, SettingsUi.Format("Settings_Load_Newer", info.BackupPath ?? "")),
            LoadStatus.Migrated => SettingsUi.Bar(InfoBarSeverity.Informational, SettingsUi.Format("Settings_Load_Migrated", info.FileVersion, info.BackupPath ?? "")),
            _ => null,
        };
    }
}
