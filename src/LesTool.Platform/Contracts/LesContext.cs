using System.Windows;

namespace LesTool.Platform;

/// <summary>
/// 命令声明的触发上下文。触发源按声明惰性获取（宿主侧 ContextProvider 实现）——
/// 只有绑定的命令真的需要时才付出取上下文的代价（UIA/剪贴板读取都有成本）。
/// </summary>
[Flags]
public enum ContextKind
{
    None = 0,
    SelectedText = 1 << 0,
    Clipboard = 1 << 1,
    Cursor = 1 << 2,
    Focus = 1 << 3,       // 2026-10-27 更名（原 ActiveWindow）：承诺值=触发前用户焦点所在窗口
    SelectedFiles = 1 << 4,
}

/// <summary>
/// 触发来源：命令由哪个通道派发。Execute 各调用点显式传入——命令可按来源分支
/// （如面板路径触发时焦点尚未回到目标应用），派发日志亦以此为准确认"谁触发的"。
/// </summary>
public enum TriggerSource
{
    Hotkey,
    CommandPalette,
    Radial,
}

/// <summary>
/// 触发会话的环境快照：触发源在"污染发生前"（面板 Show 抢焦点之前）抓取的易失字段。
/// 字段级方案——只快照会被触发过程自身污染的项（焦点窗口）；昂贵字段（选中文本）保持
/// 派发时惰性获取，稳定字段（剪贴板）无需快照。派发时快照优先，缺失回落临场获取。
/// 轮盘窗口 WS_EX_NOACTIVATE 不抢焦点，无需快照；热键无中间窗口同理。
/// </summary>
public sealed record TriggerSnapshot(nint FocusWindowHandle)
{
    public WindowTarget? Target { get; init; }
}

/// <summary>
/// 触发时随命令传递的上下文。未实现的来源保持 null——管道先通，获取器后续补
/// （SelectedText 走 UIA/Ctrl+C 兜底，仅热键与轮盘路径获取；SelectedFiles 走资源管理器选中项）。
/// 构造唯一入口为宿主的 ContextProvider.Build（含 Source），不设空单例。
/// </summary>
public sealed record LesContext(
    string? SelectedText,
    string? ClipboardText,
    double? ClipboardAgeMs,
    Point Cursor,
    string? FocusWindowTitle,
    nint FocusWindowHandle,
    IReadOnlyList<string>? SelectedFiles,
    TriggerSource Source);
