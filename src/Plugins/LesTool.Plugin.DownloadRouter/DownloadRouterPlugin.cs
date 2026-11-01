using System.IO;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.DownloadRouter;

/// <summary>
/// 下载归档插件入口（与空格预览同构：自触发核心 + 一条手动命令 + 设置页）。
/// 哨兵（文件事件驱动）随插件装载上岗；命令 downloads.organize 注册后
/// 热键/命令面板/轮盘三路可绑；启动按档位补扫（默认提示不动手）。
/// 双开关分工：宿主 PluginState 总闸管"装不装载"（重启生效）；本插件 enabled 管在岗暂停（即时）。
/// </summary>
public sealed class DownloadRouterPlugin : IAsyncLesPlugin
{
    internal const string Glyph = "\uE896"; // Segoe MDL2 Download

    private RouterEngine? _engine;
    private MoveLedger? _ledger;
    private DownloadSentinel? _sentinel;

    public string Id => "downloads";

    public void Initialize(ILesHost host)
    {
        var settings = host.Settings;

        _engine = new RouterEngine();
        _engine.SwapRules(settings.Get("rules", new List<RouteRule>()));

        _ledger = new MoveLedger(settings);
        host.OwnCleanup(() => _ledger?.FlushIfNeeded(force: true));
        _sentinel = new DownloadSentinel(_engine, _ledger, LoadConfig(settings), msg => host.Toast(msg));
        host.OwnCleanup(() => _sentinel?.Dispose());
        if (host is IPluginLifetimeHost lifetime) lifetime.Lifetime.OnStopping(() => _sentinel?.Pause());

        host.RegisterCommand("downloads.organize", "立即整理下载", Glyph,
            _ =>
            {
                int queued = _sentinel?.Sweep(MoveVia.Manual) ?? 0;
                if (queued == 0) host.Toast("下载夹没有可整理的文件");
                else host.Toast($"整理已开始：{queued} 个候选，安静确认后逐一归档", 2500);
            },
            keywords: "整理 下载 归档 分类 download organize");

        host.RegisterSettingsPage(() => new RouterSettingsPage(settings, _engine!, _ledger!, _sentinel!));

        if (settings.Get("enabled", true))
        {
            _sentinel.Start();
            RunStartupSweep(settings, host);
        }
        else
        {
            Log.Information("下载归档：开关为关，哨兵待命（设置页可恢复）");
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_sentinel is not null) await _sentinel.StopAsync();
        if (_ledger is not null) await _ledger.FlushAsync(force: true);
    }

    public void Shutdown()
    {
        _sentinel?.Dispose(); // 内部含：等在飞搬运收尾 + 账本强落
        _ledger?.FlushIfNeeded(force: true);
    }

    internal static SentinelConfig LoadConfig(IPluginSettings settings) => SentinelConfig.Parse(
        settings.Get("watchFolder", DefaultWatchFolder()),
        settings.Get("debounceSeconds", 5),
        settings.Get("fastLaneExts", ""));

    internal static string DefaultWatchFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// 启动补扫档位：Off 不看；Prompt 只数不动、有候选才提示（确认动作 = 手动执行
    /// 「立即整理下载」，三路触发皆可达）；Auto 直接补扫。延迟 3 秒避开启动高峰。
    /// </summary>
    private void RunStartupSweep(IPluginSettings settings, ILesHost host)
    {
        string raw = settings.Get("sweepMode", nameof(SweepMode.Prompt));
        if (!Enum.TryParse<SweepMode>(raw, out var mode) || mode == SweepMode.Off) return;

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        host.OwnCleanup(timer.Stop);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!host.CanDispatch()) return;
            try
            {
                if (mode == SweepMode.Auto)
                {
                    _sentinel?.Sweep(MoveVia.Startup);
                }
                else
                {
                    int candidates = _sentinel?.CountCandidates() ?? 0;
                    if (candidates > 0)
                    {
                        Log.Information("启动清点：{Count} 个可归档候选（提示档，不动手）", candidates);
                        host.Toast($"下载夹有 {candidates} 个文件可归档——执行「立即整理下载」即可处理", 4000);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动补扫/清点失败");
            }
        };
        timer.Start();
    }
}
