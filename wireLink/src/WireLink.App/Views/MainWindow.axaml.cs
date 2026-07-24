using Avalonia;
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

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

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

    private void OnOpened(object? sender,EventArgs e)
    {
        Opened -= OnOpened;

        var screen=Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if(screen is null) return;

        // Window dimensions are device-independent pixels, while WorkingArea uses
        // physical pixels. Leave room for the native title bar and window border.
        var scaling=screen.Scaling;
        var maxWidth=Math.Max(MinWidth,(screen.WorkingArea.Width/scaling)-32);
        var maxHeight=Math.Max(MinHeight,(screen.WorkingArea.Height/scaling)-48);
        Width=Math.Min(Width,maxWidth);
        Height=Math.Min(Height,maxHeight);

        var physicalWidth=(int)Math.Ceiling(Width*scaling);
        var physicalHeight=(int)Math.Ceiling((Height+32)*scaling);
        Position=new PixelPoint(
            screen.WorkingArea.X+Math.Max(0,(screen.WorkingArea.Width-physicalWidth)/2),
            screen.WorkingArea.Y+Math.Max(0,(screen.WorkingArea.Height-physicalHeight)/2));
    }

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
