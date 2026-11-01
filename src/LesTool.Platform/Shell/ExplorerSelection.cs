using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using Serilog;

namespace LesTool.Platform;

/// <summary>前台窗口所属的资源管理器形态。</summary>
public enum ExplorerKind
{
    None,
    ExplorerWindow, // CabinetWClass / ExploreWClass
    Desktop,        // Progman / WorkerW
}

/// <summary>
/// 资源管理器选中项探测（平台共享工具：空格预览插件与宿主命令上下文共用）。
/// 前台判定只做 GetForegroundWindow/GetClassName 快调用，可放钩子线程；
/// 选中项获取走 UIA（跨进程 COM，几十至几百毫秒），严禁在钩子线程调用——
/// 空格预览的"先吞后验"路径里由 UI 线程异步执行。
/// 选中项 UIA 只给显示名（隐藏扩展名设置下不含扩展名）：先直接拼路径校验存在性，
/// 不存在则在该文件夹内做"唯一前缀匹配"还原真实文件名，歧义视为失败。
/// </summary>
public static class ExplorerSelection
{
    /// <summary>选中项快照：完整路径列表 + 首项屏幕物理矩形（虚拟化或异常时为 Empty，定位兜底用）。</summary>
    public sealed record Selection(IReadOnlyList<string> Paths, Rect FirstItemBoundsPhys);

    /// <summary>前台窗口是否资源管理器（窗口或桌面）。本机快调用，可在钩子线程使用。</summary>
    public static bool IsForegroundExplorer() => ForegroundKind() != ExplorerKind.None;

    /// <summary>前台窗口句柄（本机快调用）。</summary>
    public static nint ForegroundWindowHandle() => GetForegroundWindow();

    /// <summary>前台资源管理器窗口的物理矩形（快调用）。非资源管理器前台返回 false。</summary>
    public static bool TryGetForegroundWindowRect(out Rect rect)
    {
        nint hwnd = GetForegroundWindow();
        rect = Rect.Empty;
        if (hwnd == 0 || ForegroundKind() == ExplorerKind.None) return false;
        if (!GetWindowRect(hwnd, out var r)) return false;
        rect = new Rect(r.left, r.top, r.right - r.left, r.bottom - r.top);
        return true;
    }

    /// <summary>
    /// 焦点是否在前台资源管理器的编辑框内（重命名框/地址栏/搜索框）——UIA 慢调用，
    /// 仅供吞键后的异步验证：此时空格是正常输入，应回放而非预览。
    /// </summary>
    public static bool IsForegroundFocusInEdit()
    {
        try
        {
            if (ForegroundKind() == ExplorerKind.None) return false;
            var focused = AutomationElement.FocusedElement;
            return focused is not null && focused.Current.ControlType == ControlType.Edit;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "焦点元素查询失败，按非编辑框处理");
            return false;
        }
    }

    /// <summary>
    /// 获取前台资源管理器的选中项（UIA 慢调用，UI 线程使用）。
    /// 前台非资源管理器、命名空间位置（此电脑/回收站等）、无选中、路径还原失败均返回 null。
    /// </summary>
    public static Selection? GetForegroundSelection() => GetSelection(GetForegroundWindow());

    private static Selection? GetSelection(nint hwnd)
    {
        try
        {
            switch (KindOf(hwnd))
            {
                case ExplorerKind.ExplorerWindow:
                    return FromExplorerWindow(hwnd);
                case ExplorerKind.Desktop:
                    return FromDesktop(hwnd);
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "资源管理器选中项获取失败");
            return null;
        }
    }

    public static async Task<Selection?> GetSelectionAsync(WindowTarget target, CancellationToken token = default)
    {
        var result = await UiaQueries.Executor.RunAsync(() => target.IsAlive ? GetSelection(target.Handle) : null,
            TimeSpan.FromMilliseconds(250), token);
        return result.State == QueryState.Ready && target.IsAlive ? result.Value : null;
    }

    // Unknown focus is treated as an edit control: never turn typing into a preview.
    public static async Task<bool> IsFocusInEditAsync(WindowTarget target, CancellationToken token = default)
    {
        var result = await UiaQueries.Executor.RunAsync(() =>
        {
            if (!target.IsForeground) return true;
            var focused = AutomationElement.FocusedElement;
            return focused is null || focused.Current.ProcessId != target.ProcessId || focused.Current.ControlType == ControlType.Edit;
        }, TimeSpan.FromMilliseconds(150), token);
        return result.State != QueryState.Ready || result.Value;
    }

    #region 前台判定

    private static ExplorerKind ForegroundKind() => KindOf(GetForegroundWindow());

    private static ExplorerKind KindOf(nint hwnd)
    {
        if (hwnd == 0) return ExplorerKind.None;
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, 64);
        return sb.ToString() switch
        {
            "CabinetWClass" or "ExploreWClass" => ExplorerKind.ExplorerWindow,
            "Progman" or "WorkerW" => ExplorerKind.Desktop,
            _ => ExplorerKind.None,
        };
    }

    #endregion

    #region 窗口 / 桌面两条获取路径

    private static Selection? FromExplorerWindow(nint hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);

        // 文件夹路径：地址栏 Edit 的 ValuePattern.Value 恰为完整路径（焦点不在地址栏时也在）。
        // 地址栏 Name 本地化（"地址："/"Address:"）不稳，按"值长得像路径"识别，Name 像地址的优先。
        string? folder = null;
        foreach (AutomationElement edit in root.FindAll(TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
        {
            string? value = TryGetValue(edit);
            if (value is null || !LooksLikeAbsolutePath(value)) continue;
            if (edit.Current.Name.StartsWith("地址", StringComparison.Ordinal)
                || edit.Current.Name.StartsWith("Address", StringComparison.OrdinalIgnoreCase))
            {
                folder = value; // 命中地址栏，直接定案
                break;
            }
            folder ??= value; // 退而求其次：第一个值像路径的 Edit（搜索框残留路径文本的罕见场景）
        }
        if (folder is null)
        {
            Log.Debug("未能在资源管理器窗口找到地址栏路径（特殊命名空间？），选中项获取放弃");
            return null;
        }

        return BuildSelection(root, folder);
    }

    private static Selection? FromDesktop(nint hwnd)
    {
        string? folder = GetDesktopPath();
        if (folder is null)
        {
            Log.Debug("桌面已知文件夹路径获取失败");
            return null;
        }
        return BuildSelection(AutomationElement.FromHandle(hwnd), folder);
    }

    /// <summary>在窗口 UIA 树中找支持 SelectionPattern 的列表，取选中显示名还原完整路径。</summary>
    private static Selection? BuildSelection(AutomationElement root, string folder)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        AutomationElement? list = null;
        foreach (AutomationElement candidate in root.FindAll(TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List)))
        {
            if (candidate.TryGetCurrentPattern(SelectionPattern.Pattern, out object? pattern)
                && pattern is SelectionPattern)
            {
                list = candidate;
                break;
            }
        }
        if (list is null)
        {
            Log.Debug("资源管理器窗口内未找到支持选中模式的列表");
            return null;
        }

        var names = new List<string>();
        Rect firstBounds = Rect.Empty;
        var selectionPattern = (SelectionPattern)list.GetCurrentPattern(SelectionPattern.Pattern);
        foreach (AutomationElement item in selectionPattern.Current.GetSelection())
        {
            var current = item.Current;
            names.Add(current.Name);
            if (firstBounds.IsEmpty && !current.BoundingRectangle.IsEmpty)
                firstBounds = current.BoundingRectangle; // UIA 坐标即物理像素
        }
        if (names.Count == 0)
        {
            Log.Debug("列表选中项为空（sw {Ms}ms）", sw.ElapsedMilliseconds);
            return null;
        }

        var paths = new List<string>();
        foreach (string name in names)
        {
            string? resolved = ResolvePath(folder, name);
            if (resolved is not null)
                paths.Add(resolved);
        }
        if (paths.Count == 0)
        {
            Log.Debug("选中项路径全部还原失败（首项「{First}」，sw {Ms}ms）", names[0], sw.ElapsedMilliseconds);
            return null;
        }

        Log.Debug("资源管理器选中项获取完成：{Count} 项，{Ms}ms", paths.Count, sw.ElapsedMilliseconds);
        return new Selection(paths, firstBounds);
    }

    /// <summary>显示名 → 完整路径：直接拼接校验；失败做"唯一前缀匹配"还原（隐藏扩展名场景），歧义放弃。</summary>
    private static string? ResolvePath(string folder, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        string direct = Path.Combine(folder, displayName);
        if (File.Exists(direct) || Directory.Exists(direct)) return direct;

        // 显示名无扩展名（系统隐藏已知类型扩展名）：在真实条目里找"以显示名开头"且唯一者
        string? match = null;
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(folder);
            foreach (string entry in entries)
            {
                string file = Path.GetFileName(entry);
                if (!file.StartsWith(displayName, StringComparison.OrdinalIgnoreCase)) continue;
                if (match is not null) return null; // 多个候选：歧义，放弃
                match = entry;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "前缀匹配枚举失败：{Folder}", folder);
            return null;
        }
        return match;
    }

    private static string? TryGetValue(AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern)
                   && pattern is ValuePattern value
                ? value.Current.Value
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>盘符路径（D:\…）或 UNC（\\server\share\…）。</summary>
    private static bool LooksLikeAbsolutePath(string s) =>
        (s.Length >= 3 && char.IsLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/'))
        || (s.Length >= 2 && s[0] == '\\' && s[1] == '\\');

    private static string? GetDesktopPath()
    {
        Guid folderId = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"); // FOLDERID_Desktop
        if (SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out IntPtr path) != 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            CoTaskMemFree(path);
        }
    }

    #endregion

    #region P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, nint hToken, out IntPtr ppszPath);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    #endregion
}
