using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace LesTool.Platform;

/// <summary>
/// 悬停窗口检测：在指定物理坐标处找"用户眼中的那个窗口"。
/// EnumWindows 按 Z 序自顶向下，取第一个包含该点的可见顶层窗口。
/// 只排除调用方给出的句柄集合（如截图遮罩自身，按 HWND 精确排除，
/// 本应用主窗口仍可被识别）；桌面壁纸层（Progman/WorkerW）不算窗口，
/// 由调用方兜底框整个显示器。
/// v1 只识别顶层窗口，不深入内部控件（浏览器标签页/子面板不单独识别）。
/// 平台共享工具：宿主与插件共同消费，属 LesTool.Platform 契约面。
/// </summary>
public static class WindowDetector
{
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9; // 可见边框（不含阴影和透明 resize 边）
    private const int DWMWA_CLOAKED = 14;              // 挂起的 UWP / 其他虚拟桌面里的窗

    /// <summary>返回物理像素矩形；该点无合适窗口时返回 null。</summary>
    public static Rect? FindAtPoint(Point phys, IReadOnlySet<IntPtr> excludedHandles)
    {
        Rect? found = null;
        EnumWindows((hwnd, _) =>
        {
            if (excludedHandles.Contains(hwnd)) return true; // 遮罩窗自己永远在最顶层，跳过
            if (!IsWindowVisible(hwnd)) return true;
            if (IsCloaked(hwnd)) return true;

            var sb = new StringBuilder(64);
            GetClassName(hwnd, sb, 64);
            if (sb.ToString() is "Progman" or "WorkerW") return true; // 桌面壁纸/图标层

            if (GetVisibleBounds(hwnd) is not { } bounds) return true;
            if (bounds.Width < 4 || bounds.Height < 4) return true;
            if (bounds.Contains(phys))
            {
                found = bounds;
                return false; // 找到最顶层命中者，停止枚举
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
               && cloaked != 0;
    }

    /// <summary>可见边框：优先 DWM 边框，失败退回 GetWindowRect（含阴影偏移，略不准）。</summary>
    private static Rect? GetVisibleBounds(IntPtr hwnd)
    {
        int rectSize = Marshal.SizeOf<RECT>();
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, rectSize) == 0)
            return new Rect(frame.left, frame.top, frame.right - frame.left, frame.bottom - frame.top);
        return GetWindowRect(hwnd, out RECT rect)
            ? new Rect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top)
            : null;
    }

    #region P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    #endregion
}
