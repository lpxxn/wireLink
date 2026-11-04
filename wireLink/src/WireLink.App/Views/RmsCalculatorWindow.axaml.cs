using Avalonia.Controls;
using WireLink.App.ViewModels;

namespace WireLink.App.Views;

public partial class RmsCalculatorWindow : Window
{
    public RmsCalculatorWindow()
    {
        InitializeComponent();
        DataContext = new RmsCalculatorViewModel();
    }
}
