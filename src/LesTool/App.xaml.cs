using System.IO;
using System.Windows;
using LesTool.Services;
using Serilog;

namespace LesTool;

public partial class App : Application
{
    private MouseHookService? _mouseHook;
    private RadialWindowController? _radial;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LesTool");
        Directory.CreateDirectory(Path.Combine(dataDir, "logs"));
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(dataDir, "logs", "lestool-.log"),
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
        AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
            Log.Debug(args.Exception, "首现异常 {Type}", args.Exception.GetType().Name);

        Log.Information("========== LesTool 启动 ==========");

        var window = new MainWindow();
        MainWindow = window;
        // 隐藏的轮盘窗口仍算"打开"，OnLastWindowClose 不会触发，这里显式退出
        window.Closed += (_, _) => Shutdown();
        window.Show();
        Log.Information("主窗口已显示");

        // 轮盘：右键按住拖动超过 5px 显示，松开隐藏
        var trigger = new RadialTrigger();
        _radial = new RadialWindowController();
        _radial.Initialize();
        trigger.Triggered += point => _radial.ShowAt(point);
        trigger.Released += () => _radial.Hide();

        _mouseHook = new MouseHookService(trigger);
        _mouseHook.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mouseHook?.Dispose(); // 先停钩子，窗口随后统一销毁
        Log.Information("========== LesTool 退出 ==========");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
