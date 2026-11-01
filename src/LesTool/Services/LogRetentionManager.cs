using System.IO;
using Serilog;

namespace LesTool.Services;

public enum LogRetentionMode { Daily, Weekly, Monthly }

/// <summary>日志自动清理：日志按天滚动一个文件，周期即保留天数（每天=1、每周=7、每月=30）。
/// Serilog 的 retainedFileCountLimit 在 sink 打开文件与跨天滚动时执行删除，
/// 故切换周期 = 以新上限重建 Logger，旧文件在重建后的首次落盘被清掉。</summary>
public static class LogRetentionManager
{
    private static readonly string LogsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LesTool", "logs");

    public static LogRetentionMode Mode { get; private set; } = LogRetentionMode.Weekly;

    /// <summary>周期对应的保留文件数（一天一个文件）。</summary>
    public static int RetainedFiles(LogRetentionMode mode) => mode switch
    {
        LogRetentionMode.Daily => 1,
        LogRetentionMode.Monthly => 30,
        _ => 7,
    };

    public static LogRetentionMode Parse(string value) =>
        Enum.TryParse<LogRetentionMode>(value, ignoreCase: true, out var mode) ? mode : LogRetentionMode.Weekly;

    /// <summary>启动时创建 Logger（App.OnStartup 在加载设置后调用）。</summary>
    public static void Configure()
    {
        Mode = Parse(App.Settings.LogRetention);
        Directory.CreateDirectory(LogsDir);
        Log.Logger = BuildLogger();
        Log.Information("日志：按天分文件，自动清理 {Mode}（保留 {Files} 个）", Mode, RetainedFiles(Mode));
    }

    /// <summary>设置页切换：保存设置并立即重建 Logger，让新保留期当场生效（无需重启）。</summary>
    public static void SetMode(LogRetentionMode mode)
    {
        if (mode == Mode) return;
        Log.Information("日志自动清理周期切换：{Old} → {New}（保留 {Files} 个文件）", Mode, mode, RetainedFiles(mode));
        Mode = mode;
        App.Settings.LogRetention = mode.ToString();
        AppSettings.Save(App.Settings);
        RebuildLogger();
    }

    private static ILogger BuildLogger() => new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.File(
            Path.Combine(LogsDir, "lestool-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: RetainedFiles(Mode))
        .CreateLogger();

    /// <summary>换 Logger：先换后关。旧实例握着当天文件的写句柄，必须 Dispose 释放，
    /// 新 sink 才能在下次落盘时以追加方式重开同一文件；两行之间他线程的写入由
    /// Serilog 批处理 sink 自行吞掉重试，最坏丢一条日志，不致命。</summary>
    private static void RebuildLogger()
    {
        var old = Log.Logger;
        Log.Logger = BuildLogger();
        (old as IDisposable)?.Dispose(); // ILogger 接口不暴露 Dispose，具体 Logger 才有
        Log.Debug("Logger 已按保留 {Files} 个文件重建，旧文件将在本次落盘时清理", RetainedFiles(Mode));
    }
}
