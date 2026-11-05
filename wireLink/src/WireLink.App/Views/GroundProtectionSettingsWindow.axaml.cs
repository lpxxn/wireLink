using Avalonia.Controls;
using WireLink.App.ViewModels;

namespace WireLink.App.Views;

public partial class GroundProtectionSettingsWindow : Window
{
    public GroundProtectionSettingsWindow() => InitializeComponent();

    public GroundProtectionSettingsWindow(GroundProtectionSettingsViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        if (DataContext is GroundProtectionSettingsViewModel viewModel)
            await viewModel.InitializeAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
