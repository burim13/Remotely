using Microsoft.UI.Xaml;
using Remotely.Core.NetworkSetup;
using Remotely.Core.ServerManager;
using Windows.Storage.Pickers;

namespace Remotely.ServerManager.Setup;

/// <summary>
/// One set of setup services for the whole Manager (so the router mappings the wizard made are known even after the page is closed), and the rules for when those
/// mappings are renewed and removed: renewed on every poll while the server answers, removed when the server's service is stopped and when the Manager exits.
/// </summary>
internal static class SetupLifecycle
{
    private static SetupWizardDependencies? _dependencies;
    private static bool _removing;

    public static SetupWizardDependencies Dependencies => _dependencies ??= Create();

    private static SetupWizardDependencies Create()
    {
        var fake = App.Args.Fake;
        var services = SetupWizardDefaults.Create(new ElevatedSetupActions(), App.Admin, fake ? new DemoOutsideCheck() : null, fake ? [] : null);
        return new SetupWizardDependencies(services, PickPfxFileAsync);
    }

    private static async Task<string?> PickPfxFileAsync()
    {
        if (App.Main is not Window window) return null;
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        picker.FileTypeFilter.Add(".pfx");
        picker.FileTypeFilter.Add(".p12");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>Called after every poll of the server.</summary>
    public static async Task OnSnapshotAsync(MonitorSnapshot snapshot)
    {
        if (_dependencies is null) return;
        var mapping = _dependencies.Services.Mapping;
        if (mapping.Mappings.Count == 0) return;
        if (snapshot.Reachable)
        {
            await mapping.RenewDueAsync();
            return;
        }
        if (snapshot.Service is ServiceState.Stopped or ServiceState.NotInstalled or ServiceState.StopPending && !_removing)
        {
            _removing = true;
            try { await mapping.RemoveAllAsync(); }
            finally { _removing = false; }
        }
    }

    /// <summary>When the Manager exits: ask the router to drop what this run created. Waits a few seconds at most.</summary>
    public static void RemoveAllOnExit()
    {
        if (_dependencies is null || _dependencies.Services.Mapping.Mappings.Count == 0) return;
        try { Task.Run(() => _dependencies.Services.Mapping.RemoveAllAsync()).Wait(TimeSpan.FromSeconds(6)); }
        catch (AggregateException) { }
    }
}
