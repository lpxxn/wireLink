using Avalonia.Controls;
using WireLink.App.ViewModels;

namespace WireLink.App.Views;

public partial class RegisterReaderWindow : Window
{
    public RegisterReaderWindow()=>InitializeComponent();

    public RegisterReaderWindow(RegisterReaderViewModel viewModel) : this()=>DataContext=viewModel;

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
