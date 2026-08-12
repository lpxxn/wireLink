using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using WireLink.App.ViewModels;
using WireLink.Core.Communication;
using WireLink.Core.Services;

namespace WireLink.App.Views;

public partial class MainWindow : Window
{
    private IExcelExportService? _export;
    private ILogStore? _logStore;
    private LogWindow? _logWindow;
    private RegisterReaderWindow? _registerReaderWindow;
    private SlaveAddressScannerWindow? _slaveAddressScannerWindow;
    private WaveformPointDetailsWindow? _waveformPointDetailsWindow;
    private IModbusRtuClient? _client;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    public MainWindow(MainViewModel viewModel, IModbusRtuClient client, IExcelExportService export, ILogStore logStore)
        : this()
    {
        DataContext = viewModel; _client = client; _export = export; _logStore = logStore;
        viewModel.ExportRequested += OnExportRequested;
        viewModel.WaveformExportRequested += OnWaveformExportRequested;
        viewModel.ErrorDialogRequested += OnErrorDialogRequested;
        viewModel.ShowLogRequested += (_, _) => ShowLogWindow();
        viewModel.ThemeChanged += (_, theme) => (Avalonia.Application.Current as App)?.ApplyTheme(theme);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.CurrentWaveformData))
                _waveformPointDetailsWindow?.SetData(viewModel.CurrentWaveformData);
        };
        KeyDown += OnKeyDown;
    }

    private void OnPortDropDownOpened(object? sender, EventArgs e) => (DataContext as MainViewModel)?.RefreshPorts();
    private async void OnErrorDialogRequested(object? sender, ErrorDialogRequest request)
    {
        var dialog = new ErrorDialogWindow(request);
        await dialog.ShowDialog(this);
    }
    private void OnDeviceInfoHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        ShowSlaveAddressScannerWindow();
        e.Handled = true;
    }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F8 && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && DataTabs.SelectedItem == WaveformTab)
        {
            ShowWaveformPointDetailsWindow();
            e.Handled = true;
        }
        else if (e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ShowSlaveAddressScannerWindow();
            e.Handled = true;
        }
        else if (e.Key == Key.F11 && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ShowRegisterReaderWindow();
            e.Handled = true;
        }
        else if (e.Key == Key.F12 && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ShowLogWindow();
            e.Handled = true;
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        // Window dimensions are device-independent pixels, while WorkingArea uses
        // physical pixels. Leave room for the native title bar and window border.
        var scaling = screen.Scaling;
        var maxWidth = Math.Max(MinWidth, (screen.WorkingArea.Width / scaling) - 32);
        var maxHeight = Math.Max(MinHeight, (screen.WorkingArea.Height / scaling) - 48);
        Width = Math.Min(Width, maxWidth);
        Height = Math.Min(Height, maxHeight);

        var physicalWidth = (int)Math.Ceiling(Width * scaling);
        var physicalHeight = (int)Math.Ceiling((Height + 32) * scaling);
        Position = new PixelPoint(
            screen.WorkingArea.X + Math.Max(0, (screen.WorkingArea.Width - physicalWidth) / 2),
            screen.WorkingArea.Y + Math.Max(0, (screen.WorkingArea.Height - physicalHeight) / 2));
    }

    private void ShowLogWindow()
    {
        if (_logStore is null) return;
        if (_logWindow is { } existing) { existing.Activate(); return; }
        _logWindow = new LogWindow(_logStore); _logWindow.Closed += (_, _) => _logWindow = null; _logWindow.Show();
    }

    private void ShowRegisterReaderWindow()
    {
        if (_client is null || DataContext is not MainViewModel mainViewModel) return;
        if (_registerReaderWindow is { } existing) { existing.Activate(); return; }
        _registerReaderWindow = new RegisterReaderWindow(new RegisterReaderViewModel(_client, mainViewModel));
        _registerReaderWindow.Closed += (_, _) => _registerReaderWindow = null;
        _registerReaderWindow.Show();
    }

    private void ShowWaveformPointDetailsWindow()
    {
        if (DataContext is not MainViewModel mainViewModel) return;
        if (_waveformPointDetailsWindow is { } existing)
        {
            existing.SetData(mainViewModel.CurrentWaveformData);
            existing.Activate();
            return;
        }

        _waveformPointDetailsWindow = new WaveformPointDetailsWindow(
            new WaveformPointDetailsViewModel(mainViewModel.CurrentWaveformData),
            _export);
        _waveformPointDetailsWindow.Closed += (_, _) => _waveformPointDetailsWindow = null;
        _waveformPointDetailsWindow.Show();
    }

    private async void ShowSlaveAddressScannerWindow()
    {
        if (_client is null || DataContext is not MainViewModel mainViewModel) return;
        if (_slaveAddressScannerWindow is { } existing) { existing.Activate(); return; }
        _slaveAddressScannerWindow = new SlaveAddressScannerWindow(
            new SlaveAddressScannerViewModel(_client, mainViewModel));
        _slaveAddressScannerWindow.Closed += (_, _) => _slaveAddressScannerWindow = null;
        await _slaveAddressScannerWindow.ShowDialog(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows.Where(window => window != this).ToArray())
                window.Close();
        }

        base.OnClosed(e);
    }

    private async void OnExportRequested(object? sender, ExportRequest request)
    {
        var suggested = $"{request.Title}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 Excel",
            SuggestedFileName = suggested,
            DefaultExtension = "xlsx",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Excel 工作簿") { Patterns = ["*.xlsx"] }],
        });
        if (file is null) return;
        if (_export is not null)
            await _export.ExportAsync(file.Path.LocalPath, new ExcelExportContext(request.Title, request.Values, request.ReadAt, request.RecordType, request.RecordIndex));
    }

    private async void OnWaveformExportRequested(object? sender, WaveformExportRequest request)
    {
        var suggested = $"{request.Title}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出录波 Excel",
            SuggestedFileName = suggested,
            DefaultExtension = "xlsx",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Excel 工作簿") { Patterns = ["*.xlsx"] }],
        });
        if (file is null || _export is null) return;
        await _export.ExportAsync(file.Path.LocalPath, new WaveformExcelExportContext(request.Title, request.Data));
    }
}
