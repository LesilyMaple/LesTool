using System.IO;
using System.Windows;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.DownloadRouter;

/// <summary>
/// 搬运账本：每笔移动/失败记账，新条目在前，封顶 500 条。防抖落盘（2 秒），
/// 补扫批量记账不逐笔重写文件；关停强落。后台巡检线程写、UI 线程读，
/// 内部一把锁，UI 取副本渲染；落盘封送 UI 线程（IPluginSettings 契约：全部方法 UI 线程调用）。
/// </summary>
public sealed class MoveLedger
{
    private const int Cap = 500;
    private const int FlushDebounceMs = 2000;
    private const string Key = "ledger";

    private readonly IPluginSettings _settings;
    private readonly object _gate = new();
    private List<LedgerEntry> _entries = new();
    private bool _dirty;
    private long _revision;
    private bool _persisting;
    private int _dirtySince; // TickCount，防抖起点以最后一笔为准

    public MoveLedger(IPluginSettings settings)
    {
        _settings = settings;
        try
        {
            _entries = new List<LedgerEntry>(settings.Get(Key, new List<LedgerEntry>()));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "账本读取失败，从空账起步");
            _entries = new List<LedgerEntry>();
        }
    }

    public void Add(LedgerEntry entry)
    {
        lock (_gate)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > Cap) _entries.RemoveRange(Cap, _entries.Count - Cap);
            _dirty = true;
            _revision++;
            _dirtySince = Environment.TickCount;
        }
    }

    /// <summary>UI 渲染用副本（新条目在前）。</summary>
    public IReadOnlyList<LedgerEntry> CopyForUi()
    {
        lock (_gate) return new List<LedgerEntry>(_entries);
    }

    /// <summary>防抖落盘；force=true 立即落（关停路径）。幂等。</summary>
    private Task? _saveTask;
    public void FlushIfNeeded(bool force = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess() && !dispatcher.HasShutdownStarted)
        { dispatcher.BeginInvoke(() => FlushIfNeeded(force)); return; }
        _ = FlushAsync(force);
    }
    public async Task FlushAsync(bool force = false)
    {
        Task? previous;
        lock (_gate) previous = _saveTask;
        if (previous is not null && !previous.IsCompleted)
        {
            if (!force) return;
            await previous;
        }
        Task task;
        lock (_gate)
        {
            if (!_dirty || _persisting || (!force && Environment.TickCount - _dirtySince < FlushDebounceMs)) return;
            var snapshot = new List<LedgerEntry>(_entries);
            var revision = _revision;
            _persisting = true;
            task = _saveTask = SaveSnapshotAsync(snapshot, revision);
        }
        await task;
    }
    private async Task SaveSnapshotAsync(List<LedgerEntry> snapshot, long revision)
    {
        try
        {
            if (_settings is IAsyncPluginSettings asyncSettings)
            {
                var result = await asyncSettings.SetAsync(Key, snapshot);
                if (!result.Succeeded) throw new System.IO.IOException(result.Error);
            }
            else _settings.Set(Key, snapshot);
            lock (_gate) if (revision == _revision) _dirty = false;
        }
        catch (Exception error) { Log.Warning(error, "账本保存失败，保留脏状态以重试"); }
        finally { lock (_gate) _persisting = false; }
    }
}
