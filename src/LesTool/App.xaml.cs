using System.IO;
using System.Windows;
using Serilog;

namespace LesTool;

public partial class App : Application
{
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

        Log.Information("========== 最小启动测试 ==========");

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        Log.Information("主窗口已显示");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("========== 退出 ==========");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
