using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Remotely.HostCore;
using Remotely.UI.Localization;
using Remotely.ViewerCore;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Remotely.App.Session.Files;

/// <summary>
/// The file transfer list for one session: progress, speed, cancel and retry per item, buttons to send files or a folder and to get files from the other PC, and drag and drop onto the
/// panel to send. Hosted by the session window (agent A5): call <see cref="Bind"/> with the session's <see cref="TransferQueue"/>, set <see cref="WindowHandle"/> and
/// <see cref="ListRemoteFiles"/>, and apply <see cref="FileTransferSettings.ApplyTo"/> to the session.
/// </summary>
public sealed partial class TransfersPanel : UserControl
{
    private TransferQueue? _queue;
    private TransfersViewModel? _viewModel;
    private FileTransferSettings _settings = FileTransferSettings.Load();

    public TransfersPanel()
    {
        InitializeComponent();
        TitleText.Text = Loc.Get("Transfers_Title");
        SendFilesText.Text = Loc.Get("Transfers_SendFiles");
        SendFolderText.Text = Loc.Get("Transfers_SendFolder");
        GetText.Text = Loc.Get("Transfers_GetFiles");
        DropHint.Text = Loc.Get("Transfers_DropHint");
        EmptyText.Text = Loc.Get("Transfers_Empty");
        OpenFolderItem.Text = Loc.Get("Transfers_OpenReceiveFolder");
        ChangeFolderItem.Text = Loc.Get("Transfers_ChangeReceiveFolder");
        ClearItem.Text = Loc.Get("Transfers_ClearFinished");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MoreButton, Loc.Get("Transfers_More"));
        ToolTipService.SetToolTip(MoreButton, Loc.Get("Transfers_More"));
    }

    /// <summary>Window handle of the window that hosts the panel; the file and folder pickers need it.</summary>
    public IntPtr WindowHandle { get; set; }

    /// <summary>How to list the other PC's share folder (for example <c>ct =&gt; session.FileLink.ListAsync(ct)</c>). Null hides nothing but makes "Get" report an error.</summary>
    public Func<CancellationToken, Task<ShareEntryEx[]>>? ListRemoteFiles { get; set; }

    /// <summary>Raised when the user picked another receive folder; the host window sets <c>session.DownloadDirectory</c> to it.</summary>
    public event Action<string>? ReceiveFolderChanged;

    /// <summary>False when the other PC accepts no files (view-only, or no file support): the buttons and the drop target are disabled.</summary>
    public bool CanTransfer
    {
        get => SendFilesButton.IsEnabled;
        set
        {
            SendFilesButton.IsEnabled = SendFolderButton.IsEnabled = GetButton.IsEnabled = value;
            Root.AllowDrop = value;
        }
    }

    public FileTransferSettings Settings => _settings;

    public void Bind(TransferQueue queue)
    {
        _queue = queue;
        Attach(new TransfersViewModel(queue));
    }

    /// <summary>Shows a list that is driven from outside a queue (the preview page).</summary>
    internal void BindPreview(TransfersViewModel viewModel) => Attach(viewModel);

    private void Attach(TransfersViewModel viewModel)
    {
        _viewModel = viewModel;
        TransferList.ItemsSource = viewModel.Rows;
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.Rows.CollectionChanged += (_, _) => RefreshSummary();
        RefreshSummary();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => RefreshSummary();

    private void RefreshSummary()
    {
        if (_viewModel is null) return;
        SummaryText.Text = _viewModel.Summary;
        EmptyText.Visibility = _viewModel.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Queues files and folders (by path) for sending. Used by the drop target and by the session window when something is dropped on the remote screen.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        if (_queue is null) return;
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p))) _queue.EnqueueUpload(path);
    }

    // ---- drag and drop ----

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!CanTransfer || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Loc.Get("Transfers_DropCaption");
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!CanTransfer || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            AddPaths(items.Select(i => i.Path));
        }
        finally { deferral.Complete(); }
    }

    /// <summary>Dragging a finished download out of the list hands the file or folder to Explorer or any other app.</summary>
    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<TransferRowViewModel>()
            .Where(r => r.Item is { State: TransferState.Completed, Direction: TransferDirection.Download, LocalPath: not null })
            .Select(r => r.Item.LocalPath!).ToList();
        if (paths.Count == 0) { e.Cancel = true; return; }
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var storage = new List<IStorageItem>();
                foreach (var path in paths)
                {
                    if (Directory.Exists(path)) storage.Add(await StorageFolder.GetFolderFromPathAsync(path));
                    else if (File.Exists(path)) storage.Add(await StorageFile.GetFileFromPathAsync(path));
                }
                request.SetData(storage);
            }
            finally { deferral.Complete(); }
        });
    }

    // ---- buttons ----

    private async void OnSendFiles(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        if (files is not null) AddPaths(files.Select(f => f.Path));
    }

    private async void OnSendFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) AddPaths([folder.Path]);
    }

    private sealed record RemoteChoice(ShareEntryEx Entry, string Label)
    {
        public override string ToString() => Label;
    }

    private async void OnGetFiles(object sender, RoutedEventArgs e)
    {
        if (_queue is null) return;
        ShareEntryEx[] entries;
        try { entries = ListRemoteFiles is null ? [] : await ListRemoteFiles(CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            await ShowMessageAsync(string.Format(Loc.Get("Transfers_ListFailed"), ex.Message));
            return;
        }
        if (entries.Length == 0) { await ShowMessageAsync(Loc.Get("Transfers_NothingShared")); return; }

        var choices = entries.Select(entry => new RemoteChoice(entry,
            entry.IsFolder ? $"{entry.Name}  ({Loc.Get("Transfers_Folder")})" : $"{entry.Name}  ({TransferFormat.Size(entry.Size)})")).ToList();
        var list = new ListView { ItemsSource = choices, SelectionMode = ListViewSelectionMode.Multiple, MinWidth = 380, MaxHeight = 360 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, Loc.Get("Transfers_GetTitle"));
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = Loc.Get("Transfers_GetTitle"), Content = list,
            PrimaryButtonText = Loc.Get("Transfers_GetButton"), CloseButtonText = Loc.Get("Transfers_Cancel"), DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        foreach (var choice in list.SelectedItems.OfType<RemoteChoice>())
            _queue.EnqueueDownload(choice.Entry.Name, choice.Entry.IsFolder, choice.Entry.Size);
    }

    private async Task ShowMessageAsync(string text)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = Loc.Get("Transfers_Title"), Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, CloseButtonText = Loc.Get("Transfers_Close") };
        await dialog.ShowAsync();
    }

    private void OnOpenReceiveFolder(object sender, RoutedEventArgs e)
    {
        var folder = _settings.EffectiveReceiveFolder;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException) { /* nothing to open */ }
    }

    private async void OnChangeReceiveFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        _settings.ReceiveFolder = folder.Path;
        _settings.Save();
        ReceiveFolderChanged?.Invoke(folder.Path);
    }

    private void OnClearFinished(object sender, RoutedEventArgs e) => _queue?.ClearFinished();
}
