using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.ClipboardHistory;

/// <summary>
/// 回贴（2026-10-18 用户拍板默认开，设置 pasteBack 可关）：条目写回剪贴板
/// （先标自发写，防历史层重录）→ 焦点还原到呼出前窗口 → 延时一拍补 Ctrl+V。
/// 还原失败或目标是管理员程序（UIPI 拦截前台转让/代按）时降级为
/// 「已复制，请手动粘贴」提示——不出错、不丢内容。
/// </summary>
internal sealed class PasteBack
{
    private const int FocusSettleMs = 120;
    private const int ClipboardRetryMax = 3;

    private readonly IPluginSettings _settings;
    private readonly ClipboardHistory _history;

    public PasteBack(IPluginSettings settings, ClipboardHistory history)
    {
        _settings = settings;
        _history = history;
    }

    /// <summary>执行回贴。plain = 丢弃富文本格式只贴纯文本（Ctrl+Enter / 行内 T 按钮）。
    /// 返回是否成功写回剪贴板（失败时已 Toast，调用方不必再动历史顺序）。</summary>
    public bool Execute(ClipItem item, nint targetHwnd, bool plain = false)
    {
        if (SelectionText.IsClipboardOperationActive) { ToastService.Show("正在取词，请稍后粘贴", 2000); return false; }
        if (!WriteItem(item, plain))
        {
            ToastService.Show("写剪贴板失败，本条未粘贴", 4000);
            return false;
        }
        AfterWrite(plain ? $"{item.Kind}（纯文本）" : $"{item.Kind}", targetHwnd);
        return true;
    }

    /// <summary>合并粘贴：文本按行拼接、文件清单合并去重（图片不参与），直接写剪贴板不入历史。</summary>
    public bool ExecuteMerged(IReadOnlyList<ClipItem> items, nint targetHwnd)
    {
        if (SelectionText.IsClipboardOperationActive) { ToastService.Show("正在取词，请稍后粘贴", 2000); return false; }
        var texts = items.Where(i => i.Kind == ClipKind.Text && !string.IsNullOrEmpty(i.Text))
            .Select(i => i.Text!).ToList();
        var files = items.Where(i => i.Kind == ClipKind.Files)
            .SelectMany(i => i.Files ?? Enumerable.Empty<string>())
            .Distinct().ToList();

        if (texts.Count == 0 && files.Count == 0)
        {
            ToastService.Show("所选条目没有可合并的内容（图片不参与合并）", 3000);
            return false;
        }

        var what = texts.Count > 0 ? $"合并文本 {texts.Count} 条" : $"合并文件 {files.Count} 个";
        if (!WriteWithRetry(what, () =>
            {
                var data = new DataObject();
                if (texts.Count > 0)
                    data.SetText(string.Join(Environment.NewLine, texts), TextDataFormat.UnicodeText);
                var drop = new System.Collections.Specialized.StringCollection();
                drop.AddRange(files.ToArray());
                if (drop.Count > 0) data.SetFileDropList(drop);
                Clipboard.SetDataObject(data, true);
            }))
        {
            ToastService.Show("写剪贴板失败，未粘贴", 4000);
            return false;
        }
        AfterWrite(what, targetHwnd);
        return true;
    }

    /// <summary>回贴尾部：还原焦点 → 核验落位 → 代按 Ctrl+V；任何一环失败降级提示。</summary>
    private void AfterWrite(string what, nint targetHwnd)
    {
        if (!_settings.Get("pasteBack", true))
        {
            Log.Debug("回贴开关已关，仅回写剪贴板");
            return;
        }

        if (targetHwnd == 0 || !IsWindow(targetHwnd))
        {
            Log.Information("回贴降级：无有效目标窗口，仅回写剪贴板");
            ToastService.Show("已复制，请手动粘贴", 2500);
            return;
        }

        // 面板刚收走前台，我们正是前台进程，把前台交还目标窗口是被系统允许的
        if (!SetForegroundWindow(targetHwnd))
        {
            Log.Information("回贴降级：焦点还原失败（句柄 {Handle}）", targetHwnd);
            ToastService.Show("已复制，请手动粘贴", 2500);
            return;
        }

        // 激活落地需要一拍，随后核验前台确实落位再代按，否则宁可让用户手动粘
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FocusSettleMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (GetForegroundWindow() != targetHwnd)
            {
                Log.Information("回贴降级：焦点未落到目标窗口（疑似管理员程序拒让前台）");
                ToastService.Show("已复制，请手动粘贴", 2500);
                return;
            }
            SendCtrlV();
            Log.Information("回贴完成：{What}", what);
        };
        timer.Start();
    }

    private bool WriteItem(ClipItem item, bool plain)
    {
        switch (item.Kind)
        {
            case ClipKind.Text:
                return WriteWithRetry("Text", () =>
                {
                    // 有富文本格式且未要求纯文本：三格式同写，贴进 Word/浏览器保持原样式
                    if (!plain && (item.Html is not null || item.Rtf is not null))
                    {
                        var data = new DataObject();
                        data.SetText(item.Text ?? "", TextDataFormat.UnicodeText);
                        if (item.Html is not null) data.SetText(item.Html, TextDataFormat.Html);
                        if (item.Rtf is not null) data.SetText(item.Rtf, TextDataFormat.Rtf);
                        Clipboard.SetDataObject(data, true);
                    }
                    else
                    {
                        Clipboard.SetText(item.Text ?? "");
                    }
                });
            case ClipKind.Files:
                return WriteWithRetry("Files", () =>
                {
                    var drop = new System.Collections.Specialized.StringCollection();
                    drop.AddRange(item.Files?.ToArray() ?? Array.Empty<string>());
                    Clipboard.SetFileDropList(drop);
                });
            case ClipKind.Image:
                var image = _history.LoadFullImage(item);
                if (image is null)
                {
                    Log.Error("回贴原图加载失败，本条未粘贴：{File}", item.ImageFile);
                    return false;
                }
                return WriteWithRetry("Image", () => Clipboard.SetImage(image));
            default:
                return false;
        }
    }

    /// <summary>带重试的剪贴板写入：先立自发写牌，重试耗尽仍失败则撤牌（防误伤下一次他人更新）。</summary>
    private static bool WriteWithRetry(string what, Action write)
    {
        for (var attempt = 1; ; attempt++)
        {
            ClipboardService.MarkSelfWrite();
            try
            {
                write();
                return true;
            }
            catch (Exception ex)
            {
                if (attempt >= ClipboardRetryMax)
                {
                    Log.Error(ex, "回写剪贴板失败（重试 {N} 次）：{What}", attempt, what);
                    ClipboardService.ClearSelfWrite();
                    return false;
                }
                Thread.Sleep(50); // 抢锁竞争的小退避（罕见路径，UI 停半拍可接受）
            }
        }
    }

    // ---------- 代按 Ctrl+V（SendInput） ----------

    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;
    private const uint KeyEventfKeyUp = 0x0002;

    private static void SendCtrlV()
    {
        var inputs = new[]
        {
            Input(Key(VkControl, 0)),
            Input(Key(VkV, 0)),
            Input(Key(VkV, KeyEventfKeyUp)),
            Input(Key(VkControl, KeyEventfKeyUp)),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Input(KEYBDINPUT ki) => new() { Type = 1, U = new INPUTUNION { Ki = ki } };
    private static KEYBDINPUT Key(ushort vk, uint flags) => new() { WVk = vk, DwFlags = flags };

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public nuint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public nuint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION U;
    }

    // ---------- Win32 ----------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);
}
