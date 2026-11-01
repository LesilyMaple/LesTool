using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LesTool.Platform;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 长截图捕获区域边框（绿色，区别于录制的红框）。
/// 整窗点击穿透，滚动和点击全部到达目标应用。
/// </summary>
internal partial class LongFrameWindow : Window
{
    internal LongFrameWindow(Rect regionPhys)
    {
        InitializeComponent();

        double scale = GdiCapture.GetMonitorScale(
            new Point(regionPhys.X + regionPhys.Width / 2, regionPhys.Y + regionPhys.Height / 2));
        double b = 2 * scale; // 边框画在区域外沿
        var bounds = new Rect(
            regionPhys.X - b, regionPhys.Y - b,
            regionPhys.Width + 2 * b, regionPhys.Height + 2 * b);

        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        GdiCapture.MoveWindowPhys(helper.Handle, bounds);
        SourceInitialized += (_, _) => MakeClickThrough(helper.Handle);
        Show();
    }

    internal static void MakeClickThrough(IntPtr hwnd)
    {
        IntPtr style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        _ = SetWindowLongPtr(hwnd, GWL_EXSTYLE,
            style | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
    }

    private const int GWL_EXSTYLE = -20;
    private static readonly IntPtr WS_EX_LAYERED = 0x00080000;
    private static readonly IntPtr WS_EX_TRANSPARENT = 0x00000020;
    private static readonly IntPtr WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
