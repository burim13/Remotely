using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Remotely.HostCore;
using Remotely.UI.Localization;
using Remotely.ViewerCore;

namespace Remotely.App.Session.Files;

/// <summary>One line of the transfer list: turns a <see cref="TransferItem"/> into the words, numbers and visibility a row shows. Refreshes itself on the UI thread when the item changes.</summary>
public sealed class TransferRowViewModel : INotifyPropertyChanged
{
    private readonly DispatcherQueue _ui;
    private readonly TransferQueue? _queue;

    public TransferRowViewModel(TransferItem item, DispatcherQueue ui, TransferQueue? queue)
    {
        Item = item; _ui = ui; _queue = queue;
        item.Changed += _ => _ui.TryEnqueue(() => { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); Updated?.Invoke(); });
    }

    public TransferItem Item { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised on the UI thread after the row refreshed (the list's summary line follows).</summary>
    public event Action? Updated;

    public string Title => Item.Name;

    /// <summary>Segoe Fluent Icons glyph: upload, download, or folder.</summary>
    public string Glyph => Item.IsFolder ? "" : Item.Direction == TransferDirection.Upload ? "" : "";

    public double Percent => Item.Fraction * 100;
    public bool IsIndeterminate => Item.State == TransferState.Starting || (Item.State == TransferState.Running && Item.TotalBytes <= 0);
    public Visibility ProgressVisibility => Item.State is TransferState.Starting or TransferState.Running ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CancelVisibility => Item.IsFinished ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RetryVisibility => Item.State is TransferState.Failed or TransferState.Interrupted or TransferState.Cancelled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RevealVisibility => Item.State == TransferState.Completed && Item.Direction == TransferDirection.Download && Item.LocalPath is not null ? Visibility.Visible : Visibility.Collapsed;

    public string CancelLabel => Loc.Get("Transfers_Cancel");
    public string RetryLabel => Loc.Get("Transfers_Retry");
    public string RevealLabel => Loc.Get("Transfers_ShowInFolder");

    public string Detail => Describe(Item);

    public void Cancel() => _queue?.Cancel(Item);
    public void Retry() => _queue?.Retry(Item);

    public void Reveal()
    {
        if (Item.LocalPath is not { } path) return;
        try
        {
            var argument = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* nothing to show */ }
    }

    /// <summary>The status line for an item: progress, speed and time left while it runs, the reason when it stopped.</summary>
    public static string Describe(TransferItem item)
    {
        switch (item.State)
        {
            case TransferState.Queued:
                return Loc.Get("Transfers_Queued");
            case TransferState.Starting:
                return Loc.Get("Transfers_Starting");
            case TransferState.Running:
            {
                var parts = new List<string>
                {
                    item.TotalBytes > 0
                        ? string.Format(Loc.Get("Transfers_Progress"), TransferFormat.Size(item.BytesDone), TransferFormat.Size(item.TotalBytes))
                        : string.Format(Loc.Get("Transfers_ProgressUnknown"), TransferFormat.Size(item.BytesDone)),
                };
                if (TransferFormat.Speed(item.BytesPerSecond) is { Length: > 0 } speed) parts.Add(speed);
                if (item.Remaining is { } left) parts.Add(string.Format(Loc.Get("Transfers_Eta"), TransferFormat.Clock(left)));
                if (item.ResumedFrom > 0) parts.Add(string.Format(Loc.Get("Transfers_Resumed"), TransferFormat.Size(item.ResumedFrom)));
                return string.Join("  ·  ", parts);
            }
            case TransferState.Interrupted:
                return Loc.Get("Transfers_Interrupted");
            case TransferState.Completed:
            {
                var text = Loc.Get("Transfers_Done");
                if (item.SavedName is { Length: > 0 } saved && !string.Equals(saved, item.Name, StringComparison.Ordinal))
                    text += "  ·  " + string.Format(Loc.Get("Transfers_SavedAs"), saved);
                if (item.SkippedFiles > 0) text += "  ·  " + string.Format(Loc.Get("Transfers_Skipped"), item.SkippedFiles);
                return text;
            }
            case TransferState.Cancelled:
                return Loc.Get("Transfers_Cancelled");
            default:
                return ErrorText(item.Error, item.Message);
        }
    }

    public static string ErrorText(TransferError code, string message) => code switch
    {
        TransferError.Declined => Loc.Get("Transfers_Error_Declined"),
        TransferError.TooLarge => Loc.Get("Transfers_Error_TooLarge"),
        TransferError.QuotaExceeded => Loc.Get("Transfers_Error_QuotaExceeded"),
        TransferError.DiskFull => Loc.Get("Transfers_Error_DiskFull"),
        TransferError.NameClash => Loc.Get("Transfers_Error_NameClash"),
        TransferError.AccessDenied => Loc.Get("Transfers_Error_AccessDenied"),
        TransferError.HashMismatch => Loc.Get("Transfers_Error_HashMismatch"),
        TransferError.NotFound => Loc.Get("Transfers_Error_NotFound"),
        TransferError.Busy => Loc.Get("Transfers_Error_Busy"),
        TransferError.Protocol => Loc.Get("Transfers_Error_Protocol"),
        TransferError.UnsafeArchive => Loc.Get("Transfers_Error_UnsafeArchive"),
        TransferError.Unsupported => Loc.Get("Transfers_Error_Unsupported"),
        TransferError.SourceChanged => Loc.Get("Transfers_Error_SourceChanged"),
        TransferError.LinkLost => Loc.Get("Transfers_Interrupted"),
        _ => string.Format(Loc.Get("Transfers_Error_Other"), message),
    };
}

/// <summary>The list the panel shows: one row per item of a <see cref="TransferQueue"/>, kept in step on the UI thread.</summary>
public sealed class TransfersViewModel : INotifyPropertyChanged
{
    private readonly DispatcherQueue _ui;
    private readonly TransferQueue? _queue;

    /// <summary>Must be created on the UI thread.</summary>
    public TransfersViewModel(TransferQueue? queue)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _queue = queue;
        if (queue is null) return;
        foreach (var item in queue.Items) AddRow(item);
        queue.ItemAdded += item => _ui.TryEnqueue(() => { AddRow(item); Changed(); });
        queue.ItemRemoved += item => _ui.TryEnqueue(() =>
        {
            if (Rows.FirstOrDefault(r => r.Item == item) is { } row) Rows.Remove(row);
            Changed();
        });
    }

    public ObservableCollection<TransferRowViewModel> Rows { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Summary
    {
        get
        {
            var finished = Rows.Count(r => r.Item.IsFinished);
            return Rows.Count == 0 ? Loc.Get("Transfers_Empty") : string.Format(Loc.Get("Transfers_Summary"), Rows.Count - finished, finished);
        }
    }

    /// <summary>Adds a row for an item that is driven from outside a queue (used by the preview page).</summary>
    public void AddPreview(TransferItem item) { AddRow(item); Changed(); }

    private void AddRow(TransferItem item)
    {
        var row = new TransferRowViewModel(item, _ui, _queue);
        row.Updated += Changed;
        Rows.Add(row);
    }

    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
}
