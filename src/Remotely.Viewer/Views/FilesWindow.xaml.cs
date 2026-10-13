using System.Windows;
using Remotely.HostCore;

namespace Remotely.Viewer.Views;

public partial class FilesWindow : Window
{
    public string? Chosen { get; private set; }

    internal FilesWindow(IEnumerable<ShareEntry> files)
    {
        InitializeComponent();
        Shell.WindowFrame.Round(this);
        List.ItemsSource = files.Select(f => new { f.Name, SizeText = Format(f.Size) }).ToList();
    }

    private static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    private void OnPick(object sender, RoutedEventArgs e)
    {
        Chosen = (string)((FrameworkElement)sender).Tag;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
