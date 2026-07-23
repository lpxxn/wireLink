using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using WireLink.App.ViewModels;
using WireLink.App.Views;
using WireLink.Core.Protocol;
using WireLink.Core.Registers;
using WireLink.Core.Services;
using WireLink.Infrastructure.Export;
using WireLink.Infrastructure.Logging;
using WireLink.Infrastructure.Serial;
using WireLink.Infrastructure.Settings;

namespace WireLink.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var (logger, logStore) = AppLogging.Create();
            var trace = new SerilogProtocolTrace(logger);
            var settingsService = new JsonSettingsService();
            var settings = settingsService.LoadAsync().GetAwaiter().GetResult();
            var client = new ModbusRtuClient(new SerialPortTransport(), trace);
            var parser = new RegisterParser(trace);
            var viewModel = new MainViewModel(client, new SerialPortCatalog(),
                new DeviceDataService(client, parser, trace), new FaultRecordService(client, parser),
                settingsService, trace, settings);
            ApplyTheme(settings.Theme);
            desktop.MainWindow = new MainWindow(viewModel, new ClosedXmlExportService(), logStore);
            desktop.Exit += async (_, _) => await viewModel.DisposeAsync();
            logger.Information("WireLink 已启动；恢复设置但不自动连接串口");
        }
        base.OnFrameworkInitializationCompleted();
    }

    public void ApplyTheme(AppThemeMode mode) => RequestedThemeVariant = mode switch
    {
        AppThemeMode.Light => ThemeVariant.Light,
        AppThemeMode.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}
