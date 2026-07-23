using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using System.Diagnostics;
using WireLink.App.ViewModels;
using WireLink.Core.Services;

namespace WireLink.App.Views;

public partial class LogWindow : Window
{
    public LogWindow() => InitializeComponent();

    public LogWindow(ILogStore store) : this() => DataContext=new LogViewModel(store);

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LogViewModel { SelectedEntry: { } entry } && Clipboard is { } clipboard)
            await clipboard.SetTextAsync($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{entry.Level}] {entry.Message}");
    }

    private void OnOpenDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LogViewModel viewModel)
            Process.Start(new ProcessStartInfo(viewModel.LogDirectory) { UseShellExecute = true });
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
