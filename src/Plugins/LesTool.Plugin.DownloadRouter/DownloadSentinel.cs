using System.IO;
using Serilog;

namespace LesTool.Plugin.DownloadRouter;

/// <summary>
/// 下载哨兵：FileSystemWatcher 监听 + 待运池 + 1 秒巡检，是全插件唯一的搬移执行点。
/// 闸序（谁先谁后是安全契约）：
///   ① 半成品黑名单（.crdownload 等，永不碰，先于一切免等通道）；
///   ② 免等快速道——重命名落地（浏览器下载完成的标志动作）或白名单扩展名，
///      免的只是"安静等待"，独占确认照做；被占用即降级回正常防抖（免排队不免安检）；
///   ③ 安静等待——到期且大小与上轮一致且能独占打开，三证齐才动手。
/// 线程纪律：watcher 事件线程只入池（黑名单判定纯名字，无 IO）；巡检线程顺序搬移。
/// 自愈：watcher 出错（缓冲溢出/目录失联）→ ERROR 日志 → 重建监听 + 全量补扫补漏。
/// 关停四步幂等：停监听 → 停巡检 → 等在飞搬运收尾（上限 5 秒）→ 账本强落。
/// </summary>
public sealed class DownloadSentinel : IDisposable
{
    /// <summary>半成品黑名单：先于一切免等通道生效，白名单填了这些扩展名也不会被理会。</summary>
    private static readonly HashSet<string> Blacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "crdownload", "part", "tmp", "download", "opdownload", "partial", "!ut", "filepart",
    };

    private readonly RouterEngine _engine;
    private readonly Func<string, string, string> _move;
    private readonly MoveLedger _ledger;
    private readonly Action<string> _toast;
    private readonly object _gate = new();
    private readonly object _watcherGate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>待运条目。Size=-1 表示尚未见过（巡检首见时记录并起算安静窗口）。</summary>
    private sealed record Pending(string Path, long Size, DateTime DueUtc, MoveVia Via, bool FastLane);

    private volatile SentinelConfig _config;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _patrol;
    private volatile bool _running;
    private int _patrolling; // 在飞搬运维一标记：关停等待用
    private volatile bool _disposed;
    private readonly List<Task> _timerDrains = new();
    private Task? _stopTask;

    public DownloadSentinel(RouterEngine engine, MoveLedger ledger, SentinelConfig config, Action<string> toast)
        : this(engine, ledger, config, toast, FileMover.Move) { }

    internal DownloadSentinel(RouterEngine engine, MoveLedger ledger, SentinelConfig config, Action<string> toast, Func<string, string, string> move)
    {
        _move = move;
        _engine = engine;
        _ledger = ledger;
        _config = config;
        _toast = toast;
    }

    public bool IsRunning => _running;

    /// <summary>启动（幂等）。设置页"暂停键"恢复与插件装载共用此口。</summary>
    public void Start()
    {
        if (_running || _disposed) return;
        _running = true;
        StartWatcher();
        _patrol = new System.Threading.Timer(_ => PatrolTick(), null, 1000, 1000);
        Log.Information("下载哨兵上岗：{Folder}（防抖 {Sec} 秒）", _config.WatchFolder, _config.DebounceSeconds);
    }

    /// <summary>暂停（幂等）：停监听停巡检，待运池保留，账本不强制落盘（下次 Flush 兜底）。</summary>
    public void Pause()
    {
        if (!_running) return;
        _running = false;
        StopWatcher();
        var timer = Interlocked.Exchange(ref _patrol, null);
        if (timer is not null) { lock (_gate) _timerDrains.Add(timer.DisposeAsync().AsTask()); }
        Log.Information("下载哨兵暂停（待运池剩 {Count} 个）", PendingCount());
    }

    /// <summary>参数热更新：整体换快照；监视文件夹变了才重建监听。</summary>
    public void UpdateConfig(SentinelConfig config)
    {
        bool folderChanged = !string.Equals(config.WatchFolder, _config.WatchFolder, StringComparison.OrdinalIgnoreCase);
        _config = config;
        if (_running && folderChanged)
        {
            StartWatcher();
            Log.Information("监视文件夹已切换：{Folder}", config.WatchFolder);
        }
    }

    /// <summary>
    /// 补扫（手动整理/启动补扫共用）：枚举监视夹顶层文件全部入池，走与实时事件同一条管道——
    /// 天然继承黑名单与安静检测，不会把正在下载的半成品搬走。IO 量小（仅枚举），调用线程执行。
    /// </summary>
    public int Sweep(MoveVia via)
    {
        string folder = _config.WatchFolder;
        if (!Directory.Exists(folder))
        {
            Log.Warning("补扫跳过：监视文件夹不存在 {Folder}", folder);
            return 0;
        }
        int queued = 0;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (Enqueue(file, via)) queued++;
        }
        Log.Information("补扫入池：{Count} 个候选（{Via}）", queued, via);
        return queued;
    }

    /// <summary>启动清点（Prompt 档）：只数不动。返回可归档候选数。</summary>
    public int CountCandidates()
    {
        string folder = _config.WatchFolder;
        if (!Directory.Exists(folder)) return 0;
        int n = 0;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetFileName(file);
            if (IsBlacklisted(name)) continue;
            if (_engine.TryMatch(name) is not null) n++;
        }
        return n;
    }

    public Task StopAsync()
    {
        lock (_gate) return _stopTask ??= StopCoreAsync();
    }
    private async Task StopCoreAsync()
    {
        _disposed = true; _running = false;
        StopWatcher();
        var timer = Interlocked.Exchange(ref _patrol, null);
        if (timer is not null) _timerDrains.Add(timer.DisposeAsync().AsTask());
        // Timer.DisposeAsync completes only after callbacks (including the actual move) return.
        await Task.WhenAll(_timerDrains).ConfigureAwait(false);
        Log.Information("下载哨兵已停止，全部在飞搬运结束");
    }
    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    // ---- 事件接入（线程池线程：只判定纯名字 + 入池，无 IO 无 UI）----

    private void OnFileCreated(object sender, FileSystemEventArgs e) => Enqueue(e.FullPath, MoveVia.Auto);

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        Remove(e.OldFullPath);
        // 重命名落地 = 下载完成的标志动作（.crdownload → 成品），直接走快速道
        Enqueue(e.FullPath, MoveVia.Auto, fastLane: true);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e) => Remove(e.FullPath);

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Log.Error(e.GetException(), "下载哨兵监听异常（缓冲溢出或目录失联），自愈：重建监听并全量补扫");
        if (_running)
        {
            StartWatcher();
            Sweep(MoveVia.Auto);
        }
    }

    /// <summary>入池（ Created/Renamed/补扫共用）。返回是否入池成功。</summary>
    private bool Enqueue(string path, MoveVia via, bool fastLane = false)
    {
        string name = Path.GetFileName(path);
        if (IsBlacklisted(name))
        {
            Log.Debug("黑名单半成品，忽略：{Name}", name);
            return false;
        }
        var cfg = _config;
        bool fast = fastLane || cfg.FastLaneExts.Contains(ExtensionOf(name));
        var due = fast ? DateTime.UtcNow : DateTime.UtcNow.AddSeconds(cfg.DebounceSeconds);
        lock (_gate)
        {
            if (_disposed || (!_running && via == MoveVia.Auto)) return false; // 暂停期间不再收自动事件
            _pending[path] = new Pending(path, -1, due, via, fast);
        }
        if (fast) Log.Debug("快速道入池（免安静等待，独占确认照做）：{Name}", name);
        return true;
    }

    private void Remove(string path)
    {
        lock (_gate) _pending.Remove(path);
    }

    private void StartWatcher()
    {
        lock (_watcherGate)
        {
        if (_disposed || !_running) return;
        StopWatcher();
        string folder = _config.WatchFolder;
        if (!Directory.Exists(folder))
        {
            Log.Warning("监视文件夹不存在，哨兵待命（设置页修正后自动恢复）：{Folder}", folder);
            return;
        }
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,          // v1 只看顶层
            InternalBufferSize = 64 * 1024,         // 事件只记路径不做事，缓冲给足防溢出
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        };
        watcher.Created += OnFileCreated;
        watcher.Renamed += OnFileRenamed;
        watcher.Deleted += OnFileDeleted;
        watcher.Error += OnWatcherError;
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
        }
    }

    private void StopWatcher()
    {
        lock (_watcherGate)
        {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null) return;
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        }
    }

    // ---- 巡检（唯一搬移点；1 秒一拍，重入保护）----

    private void PatrolTick()
    {
        if (!_running || Interlocked.CompareExchange(ref _patrolling, 1, 0) != 0) return;
        try
        {
            List<Pending> due;
            lock (_gate) due = _pending.Values.Where(p => p.DueUtc <= DateTime.UtcNow).ToList();

            int moved = 0, failed = 0;
            string? lastDest = null;
            foreach (var p in due)
            {
                if (!_running) break;
                var outcome = Process(p, ref lastDest);
                if (outcome == Outcome.Moved) moved++;
                else if (outcome == Outcome.Failed) failed++;
            }
            _ledger.FlushIfNeeded();
            Report(moved, failed, lastDest);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "下载哨兵巡检异常（本拍跳过，下拍自续）");
        }
        finally
        {
            Interlocked.Exchange(ref _patrolling, 0);
        }
    }

    private enum Outcome { Kept, Moved, Failed }

    /// <summary>处理单个到期条目：三证（到期/大小稳/独占）齐则搬，不齐续期，证伪则出池。</summary>
    private Outcome Process(Pending p, ref string? lastDest)
    {
        FileInfo info;
        try { info = new FileInfo(p.Path); }
        catch (Exception ex)
        {
            Log.Debug(ex, "待运文件取信息失败，出池：{Path}", p.Path);
            Remove(p.Path);
            return Outcome.Kept;
        }
        if (!info.Exists) { Remove(p.Path); return Outcome.Kept; }            // 已被别人拿走
        if ((info.Attributes & FileAttributes.Hidden) != 0)
        {
            Log.Debug("隐藏文件不出池不搬，忽略：{Name}", info.Name);
            Remove(p.Path);
            return Outcome.Kept;
        }
        if (p.Size < 0)                                                        // 首见：记录大小起算
        {
            Extend(p, info.Length, quiet: !p.FastLane);
            return Outcome.Kept;
        }
        if (!p.FastLane && info.Length != p.Size)                              // 还在长：续期
        {
            Log.Debug("文件仍在变化，防抖续期：{Name}", info.Name);
            Extend(p, info.Length, quiet: true);
            return Outcome.Kept;
        }
        if (!TryExclusiveOpen(p.Path))                                         // 没撒手：快速道降级续期
        {
            Log.Debug("文件仍被占用，{Lane}：{Name}", p.FastLane ? "快速道降级回防抖" : "防抖续期", info.Name);
            Extend(p, p.Size, quiet: true);
            return Outcome.Kept;
        }
        var rule = _engine.TryMatch(info.Name);
        if (rule is null)                                                      // 无规则认领：不动，出池
        {
            Log.Debug("无规则认领，不动：{Name}", info.Name);
            Remove(p.Path);
            return Outcome.Kept;
        }
        try
        {
            string dest = _move(p.Path, rule.TargetDir);
            _ledger.Add(new LedgerEntry(DateTime.Now, info.Name, p.Path, dest, rule.Name, MoveOutcome.Moved, p.Via, null));
            Log.Information("已归档：{File} → {Dest}（规则 {Rule}，{Via}）", info.Name, dest, rule.Name, p.Via);
            Remove(p.Path);
            lastDest = dest;
            return Outcome.Moved;
        }
        catch (Exception ex)
        {
            // 失败不无限重试：出池放弃本轮；源文件原地保留，文件再变动或补扫/手动会再来
            _ledger.Add(new LedgerEntry(DateTime.Now, info.Name, p.Path, rule.TargetDir, rule.Name,
                MoveOutcome.Failed, p.Via, ex.Message));
            Log.Error(ex, "归档失败（源文件原地保留）：{File} → {Dir}", info.Name, rule.TargetDir);
            Remove(p.Path);
            return Outcome.Failed;
        }
    }

    /// <summary>续期/首见登记：换新条目。quiet=true 时安静窗口重起算；快速道首见只记大小不等。</summary>
    private void Extend(Pending p, long size, bool quiet)
    {
        var due = quiet ? DateTime.UtcNow.AddSeconds(_config.DebounceSeconds) : DateTime.UtcNow;
        lock (_gate) _pending[p.Path] = p with { Size = size, DueUtc = due, FastLane = quiet ? false : p.FastLane };
    }

    private static bool TryExclusiveOpen(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void Report(int moved, int failed, string? lastDest)
    {
        if (moved == 0 && failed == 0) return;
        if (moved == 1 && lastDest is not null)
            _toast($"已归档：{Path.GetFileName(lastDest)} → {Path.GetDirectoryName(lastDest)}");
        else if (moved > 1)
            _toast($"下载归档：本批移动 {moved} 个文件");
        if (failed > 0)
            _toast($"有 {failed} 个文件归档失败（源文件未动），详见账本与日志");
    }

    private int PendingCount()
    {
        lock (_gate) return _pending.Count;
    }

    private static bool IsBlacklisted(string fileName) => Blacklist.Contains(ExtensionOf(fileName));

    private static string ExtensionOf(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot < 0 ? "" : fileName[(dot + 1)..];
    }
}
