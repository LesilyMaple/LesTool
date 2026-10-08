using System.IO;
using System.Windows;
using LesTool.Services;
using LesTool.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace LesTool;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    private IHost? _host;
    private TrayIconService? _tray;
    private HotkeyService? _hotkey;
    private RadialWindowController? _radial;
    private SettingsWindow? _settingsWindow;
    private PanelWindow? _panelWindow;

    private static string DataDirPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LesTool");

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, @"Local\LesTool.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("LesTool 已在运行。", "LesTool", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        Directory.CreateDirectory(Path.Combine(DataDirPath, "logs"));
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(DataDirPath, "logs", "lestool-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "未处理 UI 异常");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "未处理非 UI 异常");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "未观察的任务异常");
            args.SetObserved();
        };

        Log.Information("========== LesTool 启动 v{Version} ==========",
            typeof(App).Assembly.GetName().Version);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders(); // 静默 WinExe 下无意义的控制台/调试日志提供器
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<GestureRecognizer>();
        builder.Services.AddSingleton<RadialWindowController>();
        builder.Services.AddSingleton<HotkeyService>();
        builder.Services.AddSingleton<TrayIconService>();
        builder.Services.AddSingleton<MouseHookService>(); // IHostedService：宿主启动时装钩子
        _host = builder.Build();

        var recognizer = _host.Services.GetRequiredService<GestureRecognizer>();
        _radial = _host.Services.GetRequiredService<RadialWindowController>();
        _hotkey = _host.Services.GetRequiredService<HotkeyService>();
        _tray = _host.Services.GetRequiredService<TrayIconService>();

        _radial.Initialize(); // 预创建隐藏轮盘窗：手势触发时只做定位 + Show

        recognizer.GestureRecognized += gesture =>
        {
            Log.Information("识别到手势 {Gesture}", gesture.Sequence);
            _radial.ShowAt(gesture.EndPoint);
        };

        _hotkey.HotkeyPressed += ShowPanel;
        _hotkey.Start();

        _tray.OpenSettingsRequested += ShowSettings;
        _tray.ExitRequested += ExitApp;
        _tray.Initialize();

        _host.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        Log.Information("启动完成");
    }

    private void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            // 关闭即释放视觉树（章程：设置页懒加载、关闭回收）
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        ActivateWindow(_settingsWindow);
    }

    private void ShowPanel()
    {
        if (_panelWindow is null)
        {
            _panelWindow = new PanelWindow();
            _panelWindow.Closed += (_, _) => _panelWindow = null;
        }
        ActivateWindow(_panelWindow);
    }

    private static void ActivateWindow(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void ExitApp() => Shutdown();

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _hotkey?.Dispose();
        try
        {
            _host?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "宿主停止异常");
        }
        _host?.Dispose();

        Log.Information("========== LesTool 退出 ==========");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
