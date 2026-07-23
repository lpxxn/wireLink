using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using WireLink.App.ViewModels;
using WireLink.Core.Services;

namespace WireLink.App.Views;

public partial class MainWindow : Window
{
    private IExcelExportService? _export;
    private ILogStore? _logStore;
    private LogWindow? _logWindow;

    public MainWindow() => InitializeComponent();

    public MainWindow(MainViewModel viewModel,IExcelExportService export,ILogStore logStore)
        : this()
    {
        DataContext=viewModel; _export=export; _logStore=logStore;
        viewModel.ExportRequested+=OnExportRequested;
        viewModel.ShowLogRequested+=(_,_)=>ShowLogWindow();
        viewModel.ThemeChanged+=(_,theme)=>(Avalonia.Application.Current as App)?.ApplyTheme(theme);
        KeyDown+=OnKeyDown;
    }

    private void OnPortDropDownOpened(object? sender,EventArgs e) => (DataContext as MainViewModel)?.RefreshPorts();
    private void OnKeyDown(object? sender,KeyEventArgs e) { if(e.Key==Key.F12) { ShowLogWindow(); e.Handled=true; } }
    private void ShowLogWindow()
    {
        if(_logStore is null) return;
        if(_logWindow is { } existing) { existing.Activate(); return; }
        _logWindow=new LogWindow(_logStore); _logWindow.Closed+=(_,_)=>_logWindow=null; _logWindow.Show(this);
    }

    private async void OnExportRequested(object? sender,ExportRequest request)
    {
        var suggested=$"{request.Title}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        var file=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title="导出 Excel", SuggestedFileName=suggested, DefaultExtension="xlsx", ShowOverwritePrompt=true,
            FileTypeChoices=[new FilePickerFileType("Excel 工作簿") { Patterns=["*.xlsx"] }],
        });
        if(file is null) return;
        if(_export is not null)
            await _export.ExportAsync(file.Path.LocalPath,new ExcelExportContext(request.Title,request.Values,request.ReadAt,request.RecordType,request.RecordIndex));
    }
}
