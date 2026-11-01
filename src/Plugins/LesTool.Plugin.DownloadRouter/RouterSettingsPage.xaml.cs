using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.DownloadRouter;

/// <summary>
/// 下载归档设置页。三区：参数（改动即时生效热更新哨兵）/ 规则（ItemsControl 行内编辑，
/// 失焦即保存，无选中态）/ 账本（只读卡片 + 定位）。构造期初值回填不当作用户操作
/// （IsLoaded 守卫，沿剪切板页惯例）。行模型为可变包装类（record 是 init-only，绑不上 TwoWay）。
/// </summary>
public partial class RouterSettingsPage : UserControl
{
    private readonly IPluginSettings _settings;
    private readonly RouterEngine _engine;
    private readonly MoveLedger _ledger;
    private readonly DownloadSentinel _sentinel;

    private List<RuleRow> _rows = new();

    public RouterSettingsPage(IPluginSettings settings, RouterEngine engine,
        MoveLedger ledger, DownloadSentinel sentinel)
    {
        _settings = settings;
        _engine = engine;
        _ledger = ledger;
        _sentinel = sentinel;
        InitializeComponent();

        EnabledCheck.IsChecked = settings.Get("enabled", true);
        WatchFolderBox.Text = settings.Get("watchFolder", DownloadRouterPlugin.DefaultWatchFolder());
        DebounceBox.Text = settings.Get("debounceSeconds", 5).ToString();
        FastLaneBox.Text = settings.Get("fastLaneExts", "");
        string sweep = settings.Get("sweepMode", nameof(SweepMode.Prompt));
        (sweep == nameof(SweepMode.Auto) ? SweepAuto
            : sweep == nameof(SweepMode.Off) ? SweepOff : SweepPrompt).IsChecked = true;

        _rows = settings.Get("rules", new List<RouteRule>()).Select(r => new RuleRow
        {
            Enabled = r.Enabled,
            Name = r.Name,
            Extensions = r.Extensions,
            NameContains = r.NameContains,
            TargetDir = r.TargetDir,
        }).ToList();
        RuleList.ItemsSource = _rows;
        RefreshLedger();
    }

    // ---- 参数区（即时生效） ----

    private void OnEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool enabled = EnabledCheck.IsChecked == true;
        _settings.Set("enabled", enabled);
        if (enabled) _sentinel.Start();
        else _sentinel.Pause();
        Log.Information("下载归档开关：{State}", enabled ? "开启" : "暂停");
    }

    private void OnOrganizeClick(object sender, RoutedEventArgs e)
    {
        int queued = _sentinel.Sweep(MoveVia.Manual);
        ToastService.Show(queued == 0 ? "下载夹没有可整理的文件" : $"整理已开始：{queued} 个候选", 2500);
    }

    private void OnWatchFolderCommitted(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        CommitFolder(WatchFolderBox.Text.Trim());
    }

    private void OnBrowseWatchFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择监视文件夹" };
        if (dlg.ShowDialog() == true)
        {
            WatchFolderBox.Text = dlg.FolderName;
            CommitFolder(dlg.FolderName);
        }
    }

    private void CommitFolder(string folder)
    {
        if (folder.Length == 0) return;
        _settings.Set("watchFolder", folder);
        _sentinel.UpdateConfig(DownloadRouterPlugin.LoadConfig(_settings));
        Log.Information("监视文件夹：{Folder}", folder);
    }

    private void OnDebounceCommitted(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        int seconds = int.TryParse(DebounceBox.Text.Trim(), out var n) ? Math.Clamp(n, 1, 300) : 5;
        DebounceBox.Text = seconds.ToString();
        _settings.Set("debounceSeconds", seconds);
        _sentinel.UpdateConfig(DownloadRouterPlugin.LoadConfig(_settings));
        Log.Information("防抖秒数：{Sec}", seconds);
    }

    private void OnFastLaneCommitted(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        string raw = FastLaneBox.Text.Trim();
        _settings.Set("fastLaneExts", raw);
        _sentinel.UpdateConfig(DownloadRouterPlugin.LoadConfig(_settings));
        Log.Information("免等白名单：{Exts}", raw.Length == 0 ? "（空）" : raw);
    }

    private void OnSweepChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton { IsChecked: true, Tag: string mode }) return;
        _settings.Set("sweepMode", mode);
        Log.Information("启动补扫档位：{Mode}", mode);
    }

    // ---- 规则区（行内编辑，失焦即保存） ----

    private void OnRuleEdited(object sender, RoutedEventArgs e) => PersistRules();

    private void OnRuleAdd(object sender, RoutedEventArgs e)
    {
        _rows.Add(new RuleRow { Name = "新规则", Enabled = true });
        RebindRules();
        PersistRules();
    }

    private void OnRuleDelete(object sender, RoutedEventArgs e) => RemoveRow(sender);

    private void OnRuleUp(object sender, RoutedEventArgs e) => MoveRow(sender, -1);

    private void OnRuleDown(object sender, RoutedEventArgs e) => MoveRow(sender, 1);

    private void MoveRow(object sender, int delta)
    {
        int index = IndexOfRow(sender);
        int to = index + delta;
        if (index < 0 || to < 0 || to >= _rows.Count) return;
        (_rows[index], _rows[to]) = (_rows[to], _rows[index]);
        RebindRules();
        PersistRules();
    }

    private void RemoveRow(object sender)
    {
        int index = IndexOfRow(sender);
        if (index < 0) return;
        _rows.RemoveAt(index);
        RebindRules();
        PersistRules();
    }

    private int IndexOfRow(object sender) =>
        sender is FrameworkElement { DataContext: RuleRow row } ? _rows.IndexOf(row) : -1;

    private void RebindRules()
    {
        RuleList.ItemsSource = null;
        RuleList.ItemsSource = _rows;
    }

    /// <summary>行 → 规则持久化 + 换引擎快照。目标文件夹为空的行是编辑中的半成品，跳过不生效。</summary>
    private void PersistRules()
    {
        var rules = _rows.Where(r => r.TargetDir.Trim().Length > 0).Select(r => new RouteRule(
            r.Name.Trim().Length == 0 ? "未命名规则" : r.Name.Trim(),
            r.Enabled, r.Extensions.Trim(), r.NameContains.Trim(), r.TargetDir.Trim())).ToList();
        _settings.Set("rules", rules);
        _engine.SwapRules(rules);
    }

    // ---- 账本区（只读，进页重建时已刷新一次） ----

    private void RefreshLedger() => LedgerList.ItemsSource = LedgerSection.Build(_ledger.CopyForUi());

    private void OnLedgerRefresh(object sender, RoutedEventArgs e) => RefreshLedger();

    private void OnLedgerReveal(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LedgerRow row })
            LedgerSection.Reveal(row.Entry);
    }
}

/// <summary>规则行模型（可变，供 TwoWay 绑定；record 是 init-only 绑不上）。</summary>
public sealed class RuleRow
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public string Extensions { get; set; } = "";
    public string NameContains { get; set; } = "";
    public string TargetDir { get; set; } = "";
}

/// <summary>账本行模型（只读投影 + 定位用原始条目）。</summary>
public sealed class LedgerRow
{
    public string Time { get; init; } = "";
    public string Outcome { get; init; } = "";
    public Brush OutcomeBrush { get; init; } = Brushes.Transparent;
    public string Main { get; init; } = "";
    public LedgerEntry Entry { get; init; } = null!;
}

/// <summary>账本区逻辑（行构建 + 定位 + 刷新）独立成类，页面类不掺展示细节。</summary>
internal static class LedgerSection
{
    public static List<LedgerRow> Build(IReadOnlyList<LedgerEntry> entries)
    {
        var app = Application.Current;
        var secondary = (Brush)app.FindResource("TextSecondary");
        var danger = (Brush)app.FindResource("DangerBrush");
        return entries.Select(e => new LedgerRow
        {
            Time = e.Time.ToString("MM-dd HH:mm"),
            Outcome = e.Outcome == MoveOutcome.Moved ? "已归档" : "失败",
            OutcomeBrush = e.Outcome == MoveOutcome.Moved ? secondary : danger,
            Main = Describe(e),
            Entry = e,
        }).ToList();
    }

    public static string Describe(LedgerEntry e)
    {
        string via = e.Via switch
        {
            MoveVia.Startup => "启动",
            MoveVia.Manual => "手动",
            _ => "自动",
        };
        string tail = e.Outcome == MoveOutcome.Moved ? e.To : $"{e.To}（{e.Detail}）";
        return $"[{via}] {e.FileName} → {tail}（规则 {e.Rule}）";
    }

    /// <summary>定位：优先最终落点，已被再次移动/删除时退回原位，两处都没了如实告知。</summary>
    public static void Reveal(LedgerEntry entry)
    {
        if (File.Exists(entry.To)) { ExplorerReveal.Reveal(entry.To); return; }
        if (File.Exists(entry.From)) { ExplorerReveal.Reveal(entry.From); return; }
        ToastService.Show("文件已不在记录的位置（可能又被移动或删除）", 3000);
    }
}

/// <summary>
/// 资源管理器定位文件（父夹打开并选中）。Shell32 PIDL 原语，无命令行拼接
/// （沿智能搜索插件 Mimosa 审计定案：文件出口一律走 API 不走命令行）。
/// </summary>
internal static class ExplorerReveal
{
    public static void Reveal(string path)
    {
        nint pidl = ILCreateFromPathW(path);
        if (pidl == 0)
        {
            Log.Error("定位失败（PIDL 为空）：{Path}", path);
            return;
        }
        try
        {
            int hr = SHOpenFolderAndSelectItems(pidl, 0, 0, 0);
            if (hr != 0) Log.Error("定位失败 hr=0x{Hr:X8}：{Path}", hr, path);
            else Log.Debug("已定位文件：{Path}", path);
        }
        finally
        {
            ILFree(pidl);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(nint pidlFolder, uint cidl, nint apidl, uint dwFlags);

    [DllImport("shell32.dll")]
    private static extern void ILFree(nint pidl);
}
