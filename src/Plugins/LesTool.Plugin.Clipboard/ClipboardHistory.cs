using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LesTool.Platform;
using LesTool.Platform.Storage;
using Serilog;

namespace LesTool.Plugin.ClipboardHistory;

/// <summary>条目大类（与平台 ClipboardEntryKind 对齐；插件侧独立枚举便于 JSON 演进）。</summary>
public enum ClipKind
{
    Text,
    Image,
    Files,
}

/// <summary>一条历史记录。文本整段入 JSON（富文本 HTML/RTF 格式随行）；
/// 图片存独立 PNG 文件，条目只带文件名与哈希。Marked 是面板多选合并的运行期标记。</summary>
public sealed class ClipItem : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    public string? Text { get; set; }
    public string? Html { get; set; }   // 富文本 HTML Format（原样含 CF_HTML 头，回写保持格式）
    public string? Rtf { get; set; }    // 富文本 RTF（同上）
    public string? Hash { get; set; }   // 图片去重键：PNG 字节 SHA-256
    public string? ImageFile { get; set; } // images\ 下的文件名
    public List<string>? Files { get; set; }
    public string? SourceApp { get; set; }
    public DateTime CopiedUtc { get; set; }
    public bool Pinned { get; set; }

    private bool _marked;
    public bool Marked
    {
        get => _marked;
        set
        {
            if (_marked == value) return;
            _marked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Marked)));
        }
    }

    /// <summary>仅 Marked 用（行内勾选视觉即时刷新；其余字段的变更都伴随整表重建）。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- 展示用只读属性（绑定一次性求值，面板短生命周期内足够） ----

    /// <summary>富文本（录制时带 HTML/RTF 格式，回写保持格式）。</summary>
    public bool IsRich => Kind == ClipKind.Text && (Html is not null || Rtf is not null);

    /// <summary>链接判定：文本以 http(s):// 或 www. 开头（宽松启发，够用为准）。</summary>
    public bool IsLink => Kind == ClipKind.Text && Text is not null && StartsWithLink(Text.TrimStart());

    private static bool StartsWithLink(string text) =>
        text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

    public string FilesSummary => Kind != ClipKind.Files || Files is null || Files.Count == 0
        ? ""
        : $"{Files.Count} 个文件：{string.Join("、", Files.Select(Path.GetFileName))}";

    public string AgeText => FormatAge(DateTime.UtcNow - CopiedUtc);

    private string FormatAge(TimeSpan age) => age.TotalSeconds switch
    {
        < 10 => "刚刚",
        < 60 => $"{(int)age.TotalSeconds} 秒前",
        < 3600 => $"{(int)age.TotalMinutes} 分钟前",
        < 86400 => $"{(int)age.TotalHours} 小时前",
        < 172800 => "昨天",
        < 604800 => $"{(int)age.TotalDays} 天前",
        _ => CopiedUtc.ToLocalTime().ToString("yyyy-MM-dd"),
    };
}

/// <summary>
/// 剪贴板历史数据层：订阅平台 Updated 广播，延迟 200ms 抄内容（躲写入方的迟交付，
/// 连发更新只抄最后一份），去重置顶，落盘防抖 2s + 关窗/退出即存。全在 UI 线程
/// （WPF 剪贴板 API 要求 STA）。顺序不变式：固定块在前（块内新→旧），其后非固定（新→旧）。
/// 用户拍板：不设上限，删除只靠手动；「跳过免记录标记」是设置开关且默认关（全记录）。
/// 存储：%APPDATA%\LesTool\plugins\clipboard\data\（history.json + images\*.png）。
/// </summary>
public sealed class ClipboardHistory
{
    private const int CaptureDelayMs = 200;
    private const int RetryDelayMs = 150;
    private const int SaveDebounceMs = 2000;
    private const int ClipboardRetryMax = 3;
    private const int MaxFormatChars = 1_000_000; // 富文本单格式保险丝（约 1MB 字符）：防 Word 式巨型 HTML 撑爆档案；不是条数上限

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IPluginSettings _settings;
    private readonly string _dataDir;
    private readonly string _historyPath;
    private readonly JsonSettingsStore<HistoryFile> _store;
    private readonly string _imageDir;
    private readonly List<ClipItem> _items = new();
    private readonly DispatcherTimer _captureTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dictionary<string, ImageSource> _previewCache = new();

    private ClipboardUpdate? _pending;
    private int _clipboardRetries;
    private bool _dirty;

    public IReadOnlyList<ClipItem> Items => _items;
    public int ImageCount => _items.Count(i => i.Kind == ClipKind.Image);

    public ClipboardHistory(IPluginSettings settings)
    {
        _settings = settings;
        _dataDir = (settings as IPluginDataDirectory)?.DataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LesTool", "plugins", "clipboard", "data");
        _historyPath = Path.Combine(_dataDir, "history.json");
        _imageDir = Path.Combine(_dataDir, "images");
        _store = new(_historyPath, () => new HistoryFile(1, new()), JsonOptions,
            value => { if (value.Version != 1 || value.Items is null) throw new UnsupportedSettingsSchemaException("历史版本不受支持"); });

        _captureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CaptureDelayMs) };
        _captureTimer.Tick += (_, _) => { _captureTimer.Stop(); TryCapture(); };
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SaveDebounceMs) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
    }

    /// <summary>载入存量并开始监听。须在 UI 线程（模块表 plugins 步骤之后由插件调用）。</summary>
    public void Start()
    {
        Load();
        ClipboardService.Updated += OnClipboardUpdated;
        Log.Information("剪贴板历史已载入：{Count} 条（图片 {Images} 张）", _items.Count, ImageCount);
    }

    /// <summary>退订 + 落盘（插件 Shutdown 调用）。</summary>
    public void Stop()
    {
        ClipboardService.Updated -= OnClipboardUpdated;
        _captureTimer.Stop();
        _saveTimer.Stop();
        SaveNow();
    }

    // ---------- 监听与抄取 ----------

    private void OnClipboardUpdated(ClipboardUpdate update)
    {
        _clipboardRetries = 0;
        if (update.FromSelf)
        {
            Log.Debug("剪贴板更新来自本应用回写，不重复记录");
            return;
        }
        if (update.Excluded && _settings.Get("excludeMarked", false))
        {
            Log.Debug("条目带免记录标记且排除开关已开，跳过");
            return;
        }
        // 重启延迟计时：写方常连发多帧（先文本后富文本等），静默期到了才抄最终形态
        _pending = update;
        _captureTimer.Stop();
        _captureTimer.Start();
    }

    private void TryCapture()
    {
        var update = _pending;
        if (update is null) return;
        try
        {
            Capture(update);
            _clipboardRetries = 0;
        }
        catch (Exception ex)
        {
            // 剪贴板被其他程序占着开不了是常态竞争：小退避重试，仍失败放弃该条（旧条目不受影响）
            if (++_clipboardRetries < ClipboardRetryMax)
            {
                Log.Debug(ex, "抄剪贴板失败（第 {N} 次），{Ms}ms 后重试", _clipboardRetries, RetryDelayMs);
                _captureTimer.Interval = TimeSpan.FromMilliseconds(RetryDelayMs);
                _captureTimer.Start();
                _captureTimer.Interval = TimeSpan.FromMilliseconds(CaptureDelayMs);
                return;
            }
            _clipboardRetries = 0;
            Log.Warning("剪贴板持续被占用，放弃记录本次更新：{Kind}", update.Kind);
        }
    }

    private void Capture(ClipboardUpdate update)
    {
        switch (update.Kind)
        {
            case ClipboardEntryKind.Text:
            {
                var text = Clipboard.GetText().TrimEnd('\0'); // 个别程序多带一个结尾空字符
                if (string.IsNullOrWhiteSpace(text)) return;
                var fresh = new ClipItem
                {
                    Kind = ClipKind.Text,
                    Text = text,
                    SourceApp = update.SourceApp,
                    CopiedUtc = update.UpdateUtc,
                };
                CaptureRichFormats(fresh);
                Record(fresh, existing => existing.Kind == ClipKind.Text && existing.Text == text);
                break;
            }
            case ClipboardEntryKind.Files:
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (files.Count == 0) return;
                Record(new ClipItem
                {
                    Kind = ClipKind.Files,
                    Files = files,
                    SourceApp = update.SourceApp,
                    CopiedUtc = update.UpdateUtc,
                }, existing => existing.Kind == ClipKind.Files && existing.Files!.SequenceEqual(files));
                break;
            }
            case ClipboardEntryKind.Image:
            {
                var source = Clipboard.GetImage();
                if (source is null) return;
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                var bytes = ms.ToArray();
                var hash = Convert.ToHexString(SHA256.HashData(bytes));

                var duplicate = _items.FirstOrDefault(i => i.Kind == ClipKind.Image && i.Hash == hash);
                if (duplicate is not null)
                {
                    MoveToTop(duplicate, update.UpdateUtc, update.SourceApp);
                    Log.Debug("图片重复复制，旧条目置顶");
                    return;
                }

                var item = new ClipItem
                {
                    Kind = ClipKind.Image,
                    Hash = hash,
                    SourceApp = update.SourceApp,
                    CopiedUtc = update.UpdateUtc,
                };
                Directory.CreateDirectory(_imageDir);
                File.WriteAllBytes(Path.Combine(_imageDir, item.Id + ".png"), bytes);
                item.ImageFile = item.Id + ".png";
                Insert(item);
                Log.Debug("记录剪贴板：Image（{Bytes} 字节，来源 {App}）", bytes.Length, update.SourceApp ?? "未知");
                break;
            }
        }
    }

    /// <summary>抄富文本格式（HTML Format / RTF）随纯文本一起存，回写时原样带回
    /// （从网页/Word 复制的，贴回 Word 不丢样式）。抓取失败或超保险丝只存纯文本，不拦入账。</summary>
    private static void CaptureRichFormats(ClipItem item)
    {
        try
        {
            if (Clipboard.ContainsText(TextDataFormat.Html))
            {
                var html = Clipboard.GetText(TextDataFormat.Html);
                if (!string.IsNullOrEmpty(html) && html.Length <= MaxFormatChars) item.Html = html;
            }
            if (Clipboard.ContainsText(TextDataFormat.Rtf))
            {
                var rtf = Clipboard.GetText(TextDataFormat.Rtf);
                if (!string.IsNullOrEmpty(rtf) && rtf.Length <= MaxFormatChars) item.Rtf = rtf;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "富文本格式抓取失败，只存纯文本");
        }
    }

    /// <summary>去重入账：同内容已存在则置顶刷新（文本连富文本格式一起随最新复制刷新），否则新增。</summary>
    private void Record(ClipItem fresh, Func<ClipItem, bool> sameContent)
    {
        var existing = _items.FirstOrDefault(sameContent);
        if (existing is not null)
        {
            existing.Html = fresh.Html;
            existing.Rtf = fresh.Rtf;
            MoveToTop(existing, fresh.CopiedUtc, fresh.SourceApp);
            return;
        }
        Insert(fresh);
        Log.Debug("记录剪贴板：{Kind}（来源 {App}）", fresh.Kind, fresh.SourceApp ?? "未知");
    }

    private void MoveToTop(ClipItem item, DateTime copiedUtc, string? sourceApp)
    {
        _items.Remove(item);
        item.CopiedUtc = copiedUtc;
        if (!string.IsNullOrEmpty(sourceApp)) item.SourceApp = sourceApp;
        Insert(item);
        MarkDirty();
    }

    /// <summary>插入保持顺序不变式：固定块在前（块内新→旧），其后非固定（新→旧）。</summary>
    private void Insert(ClipItem item)
    {
        var index = item.Pinned ? 0 : _items.TakeWhile(i => i.Pinned).Count();
        _items.Insert(index, item);
        MarkDirty();
    }

    // ---------- 持久化 ----------

    private sealed record HistoryFile(int Version, List<ClipItem> Items);

    public bool IsProtected => _store.IsProtected;
    public void ResetProtectedHistory()
    {
        _store.ResetToDefaults();
        _items.Clear(); _previewCache.Clear(); _pending = null;
        _dirty = false; _saveTimer.Stop(); _captureTimer.Stop();
    }

    private void Load()
    {
        var file = _store.Read();
        _items.AddRange(file.Items);
        if (_store.IsProtected) ToastService.Show("剪贴板历史文件不可恢复，已保护原文件并暂停保存", 4000);
    }

    private void MarkDirty()
    {
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>立即落盘（脏才写）。先写临时文件再原子替换，断电不留半份档案。</summary>
    public void SaveNow()
    {
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(_dataDir);
            _store.Save(new HistoryFile(1, _items));
            _dirty = false;
            CollectOrphanImages();
            Log.Debug("历史已保存：{Count} 条", _items.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "历史保存失败：{Path}", _historyPath);
        }
    }

    private void CollectOrphanImages()
    {
        try
        {
            if (!Directory.Exists(_imageDir)) return;
            var referenced = _items.Where(item => item.ImageFile is not null).Select(item => item.ImageFile!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(_historyPath + ".bak"))
            {
                var previous = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(_historyPath + ".bak"));
                if (previous?.Items is null) return;
                referenced.UnionWith(previous.Items.Where(item => item.ImageFile is not null).Select(item => item.ImageFile!));
            }
            foreach (var image in Directory.EnumerateFiles(_imageDir, "*.png"))
                if (!referenced.Contains(Path.GetFileName(image))) File.Delete(image);
        }
        catch (Exception error) { Log.Debug(error, "历史图片清理延后（保留有效索引引用）"); }
    }

    // ---------- 面板操作 ----------

    public void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        _items.Remove(item);
        var index = item.Pinned ? 0 : _items.TakeWhile(i => i.Pinned).Count();
        _items.Insert(index, item);
        MarkDirty();
        Log.Information("{Action}历史条目：{Kind}（来源 {App}）",
            item.Pinned ? "固定" : "取消固定", item.Kind, item.SourceApp ?? "未知");
    }

    public void Delete(ClipItem item)
    {
        if (!_items.Remove(item)) return;
        // Image files remain until a separate GC can account for live and backup indexes.
        _previewCache.Clear();
        MarkDirty();
        Log.Information("删除历史条目：{Kind}（来源 {App}）", item.Kind, item.SourceApp ?? "未知");
    }

    public void ClearAll()
    {
        _items.Clear();
        _previewCache.Clear();
        // Keep images referenced by the last durable index/backup.
        MarkDirty();
        Log.Information("剪切板历史已清空");
    }

    /// <summary>选中粘贴后的自触碰：时间刷新并提到本组顶部（面板已回写并标记自发写，不会重录）。</summary>
    public void TouchOnPaste(ClipItem item)
    {
        item.CopiedUtc = DateTime.UtcNow;
        _items.Remove(item);
        var index = item.Pinned ? 0 : _items.TakeWhile(i => i.Pinned).Count();
        _items.Insert(index, item);
        MarkDirty();
    }

    // ---------- 图片读写 ----------

    /// <summary>行预览图（解码到 128px 高 + 缓存；面板收起时清缓存控内存）。</summary>
    public ImageSource? LoadPreview(ClipItem item)
    {
        if (item.ImageFile is null) return null;
        if (_previewCache.TryGetValue(item.ImageFile, out var cached)) return cached;
        try
        {
            var path = Path.Combine(_imageDir, item.ImageFile);
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelHeight = 128; // 预览 48 DIP 的两倍余量，高分屏不发糊
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            _previewCache[item.ImageFile] = image;
            return image;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "预览图加载失败：{File}", item.ImageFile);
            return null;
        }
    }

    /// <summary>回贴用原图（完整分辨率加载）。</summary>
    public BitmapSource? LoadFullImage(ClipItem item)
    {
        if (item.ImageFile is null) return null;
        try
        {
            var path = Path.Combine(_imageDir, item.ImageFile);
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "回贴原图加载失败：{File}", item.ImageFile);
            return null;
        }
    }

    public void DropPreviewCache() => _previewCache.Clear();
}
