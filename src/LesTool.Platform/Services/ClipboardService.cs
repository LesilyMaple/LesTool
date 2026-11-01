using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Serilog;

namespace LesTool.Platform;

/// <summary>剪贴板内容大类（不展开格式细节，够用为准）。</summary>
public enum ClipboardEntryKind
{
    None,
    Text,
    Image,
    Files,
}

/// <summary>一次剪贴板更新的元数据快照（Updated 事件载荷）。</summary>
public sealed record ClipboardUpdate(ClipboardEntryKind Kind, bool Excluded, bool FromSelf, string? SourceApp, DateTime UpdateUtc);

/// <summary>
/// 最小剪贴板监听：UI 线程消息窗口（HWND_MESSAGE）收 WM_CLIPBOARDUPDATE，
/// 只记录元数据——时间戳、大类、来源进程、排除标记、自发写标记——不存内容。
/// 三个消费者：LesContext 的新鲜度（AgeMsOfLastUpdate）、剪贴板历史插件（经 Updated
/// 事件自行抄内容）、需要区分自家回写的场景（MarkSelfWrite）。
/// 密码管理器等写入时会带"排除监控"声明格式（ExcludeClipboardContentFromMonitorProcessing /
/// CanIncludeInClipboardHistory，出现即排除），此处尊重并记入 LastExcluded——
/// 是否跳过由历史插件按用户设置决定。全用 IsClipboardFormatAvailable 探测，
/// 不打开剪贴板，避免与写入方抢锁。
/// 平台服务：宿主（上下文新鲜度）与插件（自发写标记）共用。
/// </summary>
public static class ClipboardService
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const uint CF_BITMAP = 2, CF_UNICODETEXT = 13, CF_HDROP = 15;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private static HwndSource? _source;
    private static uint _excludeMonitorFormat; // ExcludeClipboardContentFromMonitorProcessing
    private static uint _excludeHistoryFormat; // CanIncludeInClipboardHistory：出现即"不要进历史"
    // 自发写标记按次数叠加而非布尔：Ctrl+C 取词会连续产生两次更新（目标程序拷贝一次、
    // 我们恢复原内容一次），两次都在消息队列里晚于同步代码处理——需要两张牌一一对应
    private sealed class SelfWrite(uint owner, uint baseline)
    {
        internal uint Owner { get; } = owner;
        internal uint Baseline { get; } = baseline;
        internal long Expires { get; } = Environment.TickCount64 + 1200;
        internal uint? Exact;
    }
    public sealed class SelfWriteReservation : IDisposable
    {
        private readonly SelfWrite _mark;
        private bool _observed;
        private SelfWriteReservation(SelfWrite mark) => _mark = mark;
        public uint? ObservedSequence => _mark.Exact;
        public void Observe(uint sequence) { _mark.Exact ??= sequence; _observed = true; }
        public void Dispose() { if (!_observed && _mark.Exact is null) _selfWrites.Remove(_mark); }
        internal static SelfWriteReservation Create(uint owner)
        {
            _selfWrites.RemoveAll(mark => mark.Expires <= Environment.TickCount64);
            var mark = new SelfWrite(owner, GetClipboardSequenceNumber()); _selfWrites.Add(mark);
            return new(mark);
        }
    }
    private static readonly List<SelfWrite> _selfWrites = new();

    /// <summary>最近一次剪贴板更新时间（UTC）；null = 启动后从未更新。</summary>
    public static DateTime? LastUpdateUtc { get; private set; }

    public static ClipboardEntryKind LastKind { get; private set; } = ClipboardEntryKind.None;

    /// <summary>最近一次内容是否带"排除监控/排除历史"声明格式（密码管理器等）。</summary>
    public static bool LastExcluded { get; private set; }

    /// <summary>最近一次更新是否为本应用自己的写入（经 MarkSelfWrite 标记）。</summary>
    public static bool LastFromSelf { get; private set; }

    /// <summary>每次剪贴板更新广播（UI 线程，元数据不含内容）。
    /// 消费者自带异常兜底——这里不包 try/catch，异常直接进全局处理器。</summary>
    public static event Action<ClipboardUpdate>? Updated;

    /// <summary>最近一次更新时的前台进程名（复制来源的近似）；取不到为 null。</summary>
    public static string? LastSourceApp { get; private set; }

    /// <summary>最近一次更新距现在的毫秒数；从未更新时 null（ContextProvider 新鲜度用）。</summary>
    public static double? AgeMsOfLastUpdate() =>
        LastUpdateUtc is { } last ? (DateTime.UtcNow - last).TotalMilliseconds : null;

    /// <summary>标记接下来的剪贴板更新为本应用自己的写入（历史面板据此跳过自家回写）。
    /// 可叠加：每次调用抵消接下来的一次更新（SelectionText 的拷贝+恢复成对使用）。</summary>
    public static SelfWriteReservation BeginSelfWrite(uint owner) => SelfWriteReservation.Create(owner);
    public static void MarkSelfWrite() => MarkSelfWrite((uint)Environment.ProcessId);
    public static void MarkSelfWrite(uint expectedOwner) => _ = BeginSelfWrite(expectedOwner);

    /// <summary>清空全部挂起的自发写标记：回写最终失败时调用，避免后续他人更新被误判为自发写。</summary>
    public static void ClearSelfWrite() => _selfWrites.Clear();

    /// <summary>须在 UI 线程调用一次。</summary>
    public static void Start()
    {
        if (_source != null) return;
        _source = new HwndSource(new HwndSourceParameters("LesTool.Clipboard") { ParentWindow = new IntPtr(-3) });
        _source.AddHook(WndProc);
        _excludeMonitorFormat = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        _excludeHistoryFormat = RegisterClipboardFormat("CanIncludeInClipboardHistory");
        if (!AddClipboardFormatListener(_source.Handle))
            Log.Error("剪贴板监听注册失败（Win32 {Error}），上下文新鲜度将不可用",
                Marshal.GetLastWin32Error());
        else
            Log.Information("剪贴板监听已启动");
    }

    public static void Shutdown()
    {
        if (_source == null) return;
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
        Log.Information("剪贴板监听已停止");
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            handled = true;
            OnClipboardUpdate();
        }
        return IntPtr.Zero;
    }

    private static void OnClipboardUpdate()
    {
        _selfWrites.RemoveAll(mark => mark.Expires <= Environment.TickCount64);
        GetWindowThreadProcessId(GetClipboardOwner(), out var owner);
        var sequence = GetClipboardSequenceNumber();
        var match = _selfWrites.FindIndex(mark => mark.Exact is { } exact ? exact == sequence : mark.Owner == owner && mark.Baseline != sequence);
        LastFromSelf = match >= 0;
        if (match >= 0) { _selfWrites[match].Exact = sequence; _selfWrites.RemoveAt(match); }
        LastUpdateUtc = DateTime.UtcNow;
        LastKind = DetectKind();
        LastExcluded = IsAvailable(_excludeMonitorFormat) || IsAvailable(_excludeHistoryFormat);
        LastSourceApp = QueryForegroundProcessName();
        Log.Debug("剪贴板更新：{Kind}，来源 {Source}，自发 {Self}，排除 {Excluded}",
            LastKind, LastSourceApp ?? "未知", LastFromSelf, LastExcluded);
        var update = new ClipboardUpdate(LastKind, LastExcluded, LastFromSelf, LastSourceApp, LastUpdateUtc.Value);
        if (Updated is not null)
            foreach (Action<ClipboardUpdate> listener in Updated.GetInvocationList())
                try { listener(update); } catch (Exception error) { Log.Error(error, "剪贴板订阅者失败"); }
    }

    private static ClipboardEntryKind DetectKind()
    {
        // 顺序：文件列表 > 文本 > 位图（复制文件时常同时带文本路径）
        if (IsAvailable(CF_HDROP)) return ClipboardEntryKind.Files;
        if (IsAvailable(CF_UNICODETEXT)) return ClipboardEntryKind.Text;
        if (IsAvailable(CF_BITMAP)) return ClipboardEntryKind.Image;
        return ClipboardEntryKind.None;
    }

    private static bool IsAvailable(uint format) =>
        format != 0 && IsClipboardFormatAvailable(format);

    private static string? QueryForegroundProcessName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out var pid);
            return pid == 0 ? null : Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return null; // 进程已退出等，元数据可缺
        }
    }
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
