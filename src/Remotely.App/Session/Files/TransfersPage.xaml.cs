using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Remotely.HostCore;
using Remotely.ViewerCore;

namespace Remotely.App.Session.Files;

/// <summary>
/// Not part of the navigation: opened with <c>Remotely.exe --show TransfersPage</c> so CI can start and photograph the transfer panel. It shows invented example transfers in every state
/// (nothing is sent anywhere) and keeps one of them moving so the live-update path is exercised too.
/// </summary>
public sealed partial class TransfersPage : Page
{
    private readonly DispatcherQueueTimer _timer;
    private readonly TransferItem _moving;
    private long _done = 310L << 20;

    public TransfersPage()
    {
        InitializeComponent();
        var viewModel = new TransfersViewModel(null);

        _moving = new TransferItem(TransferDirection.Upload, "Holiday video 2026.mp4", @"C:\Users\example\Videos\Holiday video 2026.mp4", false, 1_450L << 20);
        _moving.Begin(_moving.TotalBytes, 0);
        _moving.Progress(_done);
        viewModel.AddPreview(_moving);

        var resumed = new TransferItem(TransferDirection.Download, "Quarterly report.pdf", null, false, 48L << 20);
        resumed.Begin(48L << 20, 21L << 20);
        resumed.Progress(30L << 20);
        viewModel.AddPreview(resumed);

        var folder = new TransferItem(TransferDirection.Upload, "Fotografi të verës", @"C:\Users\example\Pictures\Fotografi të verës", true, 820L << 20);
        viewModel.AddPreview(folder);

        var done = new TransferItem(TransferDirection.Download, "Invoice.xlsx", null, false, 2L << 20);
        done.Begin(2L << 20, 0);
        done.Finish("Invoice (1).xlsx", @"C:\Users\example\Downloads\Invoice (1).xlsx");
        viewModel.AddPreview(done);

        var full = new TransferItem(TransferDirection.Upload, "Backup.zip", @"C:\Backup.zip", false, 9L << 30);
        full.Fail(TransferState.Failed, TransferError.DiskFull, "There is not enough free space for this file.");
        viewModel.AddPreview(full);

        var lost = new TransferItem(TransferDirection.Download, "Presentation.pptx", null, false, 120L << 20);
        lost.Fail(TransferState.Interrupted, TransferError.LinkLost, "");
        viewModel.AddPreview(lost);

        FilesPanel.BindPreview(viewModel);

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) =>
        {
            _done = Math.Min(_moving.TotalBytes - 1, _done + (3L << 20));
            _moving.Progress(_done);
        };
        _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }
}
