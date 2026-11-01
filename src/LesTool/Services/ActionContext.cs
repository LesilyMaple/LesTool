using System.Runtime.InteropServices;
using System.Windows;
using LesTool.Platform;
using Serilog;

namespace LesTool.Services;

/// <summary>
/// 按命令声明的 ContextKind 惰性构建 LesContext（宿主侧实现）。
/// 须在 UI 线程调用（WPF Clipboard API 要求 STA；各触发源的分发路径均满足）。
/// </summary>
internal static class ContextProvider
{
    public static LesContext Build(ContextKind kind)
    {
        string? clipboardText = null;
        double? clipboardAgeMs = null;
        Point cursor = default;
        string? activeTitle = null;
        nint activeHandle = 0;

        try
        {
            if (kind.HasFlag(ContextKind.Cursor))
                cursor = GdiCapture.GetCursorPosition();

            if (kind.HasFlag(ContextKind.ActiveWindow))
            {
                activeHandle = GetForegroundWindow();
                if (activeHandle != 0)
                {
                    var buffer = new System.Text.StringBuilder(256);
                    activeTitle = GetWindowText(activeHandle, buffer, buffer.Capacity) > 0
                        ? buffer.ToString()
                        : null;
                }
            }

            if (kind.HasFlag(ContextKind.Clipboard) && Clipboard.ContainsText())
            {
                clipboardText = Clipboard.GetText();
                clipboardAgeMs = ClipboardService.AgeMsOfLastUpdate(); // 无监听数据时为 null
            }
        }
        catch (Exception ex)
        {
            // 单项失败不影响其余字段与派发
            Log.Debug(ex, "上下文获取部分失败（{Kind}），缺失字段置空", kind);
        }

        return new LesContext(null, clipboardText, clipboardAgeMs, cursor, activeTitle, activeHandle, null);
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, System.Text.StringBuilder text, int count);
}
