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
    ActiveWindow = 1 << 3,
    SelectedFiles = 1 << 4,
}

/// <summary>
/// 触发时随命令传递的上下文。未实现的来源保持 null——管道先通，获取器后续补
/// （SelectedText 走 UIA/剪贴板兜底，SelectedFiles 走资源管理器选中项，均未实现）。
/// </summary>
public sealed record LesContext(
    string? SelectedText,
    string? ClipboardText,
    double? ClipboardAgeMs,
    Point Cursor,
    string? ActiveWindowTitle,
    nint ActiveWindowHandle,
    IReadOnlyList<string>? SelectedFiles)
{
    public static LesContext Empty { get; } = new(null, null, null, default, null, 0, null);
}
