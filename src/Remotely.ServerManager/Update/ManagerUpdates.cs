using System.Diagnostics;
using System.Reflection;
using Remotely.Core.Update;
using Remotely.UI.Localization;

namespace Remotely.ServerManager.Update;

/// <summary>
/// The one update controller of the Server Manager (opt-in, notify only). It looks for the server installer, which carries the server and the Manager. HOW TO HOST IT: call <see cref="StartBackgroundCheck"/> once after the main window exists (it does
/// nothing unless the person turned update checks on), put an <see cref="UpdateBanner"/> at the top of the shell and an <see cref="UpdateSettingsSection"/> on the
/// Settings page. Nothing here starts an installer by itself.
/// </summary>
internal static class ManagerUpdates
{
    private static readonly HttpClient Http = CreateClient();
    private static UpdateController? _controller;

    public static string DownloadFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remotely Server Manager", "updates");

    public static UpdateController Controller => _controller ??= Create();

    public static ReleaseVersion CurrentVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(ManagerUpdates).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (ReleaseVersion.TryParse(informational, out var v)) return v;
            return ReleaseVersion.TryParse(assembly.GetName().Version?.ToString(3), out var n) ? n : ReleaseVersion.Parse("0.0.0");
        }
    }

    /// <summary>Once a day at most, and only when the person switched update checks on.</summary>
    public static void StartBackgroundCheck() => _ = Task.Run(CheckIfDueSafeAsync);

    public static async Task CheckIfDueSafeAsync()
    {
        try { await Controller.CheckIfDueAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { App.LogCrash(ex); }
    }

    /// <summary>The person's own click. Hashes the file once more first; a file that changed since it was checked is not opened.</summary>
    public static async Task<bool> OpenInstallerAsync()
    {
        if (Controller.State.Package is not { } package) return false;
        if (!await package.StillMatchesAsync().ConfigureAwait(true)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(package.Path) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return true; }   // the person declined the Windows permission prompt; the file is fine
    }

    public static string Text(string key)
    {
        var text = Loc.Get(key);
        return text == key ? UpdatePresentation.English(key) : text;
    }

    public static string Text(UpdateLine line) => UpdatePresentation.Format(Text(line.Key), line.Args);

    private static UpdateController Create()
    {
        var path = UpdateSettings.PathFor("Remotely Server Manager");
        var options = new UpdateOptions();
        return new UpdateController(new UpdateChecker(Http, options), new UpdateDownloader(Http, options), UpdateSettings.Load(path), path, CurrentVersion,
            UpdateProduct.Server, "x64", DownloadFolder);
    }

    private static HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromMinutes(30) };
}
