using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WireLink.App.ViewModels;

namespace WireLink.App.Views;

public partial class ErrorDialogWindow : Window
{
    public ErrorDialogWindow()
    {
        InitializeComponent();
    }

    public ErrorDialogWindow(ErrorDialogRequest request) : this()
    {
        Title=request.Title;
        DialogTitle.Text=request.Title;
        DialogMessage.Text=request.Message;
    }

    private void OnConfirmClick(object? sender,RoutedEventArgs e)=>Close();

    private void OnHeaderPointerPressed(object? sender,PointerPressedEventArgs e)
    {
        if(e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnDialogKeyDown(object? sender,KeyEventArgs e)
    {
        if(e.Key!=Key.Escape) return;
        Close();
        e.Handled=true;
    }
}
