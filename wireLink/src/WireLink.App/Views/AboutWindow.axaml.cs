using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace WireLink.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Title = $"关于 {AppInfo.Product}";
        ProductValue.Text = AppInfo.Product;
        VersionValue.Text = AppInfo.Version;
        CompanyValue.Text = AppInfo.Company;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.F1 or Key.Enter))
            return;
        Close();
        e.Handled = true;
    }
}
