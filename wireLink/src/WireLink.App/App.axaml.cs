using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
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
            var mainWindow = new MainWindow(viewModel, new ClosedXmlExportService(), logStore);
            desktop.MainWindow = mainWindow;
            desktop.Exit += async (_, _) => await viewModel.DisposeAsync();

            // 某些终端启动场景下，桌面生命周期已经进入消息循环，
            // 但窗口没有被系统带到前台。等初始化完成后显式显示并激活一次。
            Dispatcher.UIThread.Post(() =>
            {
                if (!mainWindow.IsVisible) mainWindow.Show();
                mainWindow.Activate();
            }, DispatcherPriority.Loaded);

            mainWindow.Opened += (_, _) => logger.Information("WireLink 主窗口已显示");
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
