using Avalonia.Controls;
using Avalonia.Input;
using WireLink.App.ViewModels;

namespace WireLink.App.Views;

public partial class SlaveAddressScannerWindow : Window
{
    public SlaveAddressScannerWindow() => InitializeComponent();

    public SlaveAddressScannerWindow(SlaveAddressScannerViewModel viewModel) : this() => DataContext = viewModel;

    private void OnAddressDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: SlaveAddressScanResult result }
            && DataContext is SlaveAddressScannerViewModel viewModel)
        {
            viewModel.UseAddress(result);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
