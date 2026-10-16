namespace Remotely.App.Services;

/// <summary>
/// What pages and view models may use instead of reaching into <c>App</c> statics, so they can be tested. CONTRACT: owned by the integrator;
/// agent A2 implements <c>AppServices</c> in the shell. Pages never call <c>MessageBox</c>-style APIs, file pickers or clipboard APIs directly.
/// </summary>
public interface IAppServices
{
    void Navigate(string page);
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);
    Task ShowErrorAsync(string title, string message, string? details = null);
    void Toast(string title, string message);
    Task<string?> PickFileAsync();
    Task<string?> PickFolderAsync();
    void CopyText(string text);
    string Text(string resourceKey);
}
