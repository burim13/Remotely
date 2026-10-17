using Windows.Storage.Pickers;

namespace Remotely.App.Views.Settings;

/// <summary>"Save as" for the export buttons (IAppServices has open pickers only). Opening a file uses <c>App.Services.PickFileAsync</c>.</summary>
internal static class FileDialogs
{
    public const long MaxImportBytes = 4 * 1024 * 1024;

    /// <summary>The path the person chose, or null when they cancelled or there is no window to attach the picker to.</summary>
    public static async Task<string?> PickSavePathAsync(string suggestedName)
    {
        if (App.Main is not { } main) return null;
        var picker = new FileSavePicker { SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(main));
        return (await picker.PickSaveFileAsync())?.Path;
    }

    /// <summary>Reads a small text file chosen for import. Null when it is missing or larger than <see cref="MaxImportBytes"/>.</summary>
    public static string? ReadSmallText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxImportBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
