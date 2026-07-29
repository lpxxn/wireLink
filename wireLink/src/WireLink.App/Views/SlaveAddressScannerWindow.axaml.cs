using Avalonia.Controls;
using WireLink.App.ViewModels;

namespace WireLink.App.Views;

public partial class SlaveAddressScannerWindow : Window
{
    public SlaveAddressScannerWindow() => InitializeComponent();

    public SlaveAddressScannerWindow(SlaveAddressScannerViewModel viewModel) : this() => DataContext = viewModel;

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
