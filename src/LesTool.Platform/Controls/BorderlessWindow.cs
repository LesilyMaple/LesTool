using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace LesTool.Platform.Controls;

/// <summary>
/// 无边框窗口配方（Chromium/VS Code 同款）：给 HWND 补回 WS_THICKFRAME|WS_CAPTION 等系统样式
/// 保留 resize / Aero Snap / 最大化动画 / 任务栏交互，但拦截 WM_NCCALCSIZE 把客户区扩到整窗，
/// DWM 就不再绘制系统边框线和标题栏——圆角透明窗口四周不再出现直角残影。
/// 鼠标命中（标题栏拖拽、边缘缩放）由 WM_NCHITTEST 自报，替代 WindowChrome。
/// 平台控件：宿主与插件窗口共用（LesWindowStyle 经 Design.xaml 引用本类型）。
/// </summary>
public static class BorderlessWindow
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(BorderlessWindow),
        new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    // 与视觉规格一致：标题栏 40（CaptionBar 高度，DIP），缩放热区 10
    private const double CaptionDip = 40;
    private const double ResizeDip = 10;
    // 右上角三按钮区（3×44 + 右边距，DIP），命中测试放行给控件
    private const double ButtonsZoneWidthDip = 152;
    private const double ButtonsZoneHeightDip = 36;

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window) return;
        window.SourceInitialized += (_, _) => Attach(window);
    }

    private static void Attach(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var style = GetWindowLong(hwnd, GwlStyle);
        SetWindowLong(hwnd, GwlStyle, style
            | WsThickframe | WsCaption | WsSysmenu | WsMinimizebox | WsMaximizebox);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        Log.Debug("无边框窗口行为已挂接：0x{Hwnd}", hwnd);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcCalcSize when wParam != IntPtr.Zero:
                // 非客户区归零：DWM 边框线/系统标题栏不再绘制，客户区覆盖整窗
                handled = true;
                return IntPtr.Zero;

            case WmNcHitTest:
                handled = true;
                return HitTest(hwnd, lParam);

            case WmGetMinMaxInfo:
                // 无 WS_CAPTION 生效布局后，最大化会盖住任务栏，须钉到工作区
                if (Marshal.PtrToStructure<MinMaxInfo>(lParam) is { } mmi &&
                    MonitorFromWindow(hwnd, MonitorDefaulttonearest) != IntPtr.Zero &&
                    GetMonitorInfo(MonitorFromWindow(hwnd, MonitorDefaulttonearest), out var mi))
                {
                    mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
                    mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                    mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                    Marshal.StructureToPtr(mmi, lParam, false);
                }
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private static IntPtr HitTest(IntPtr hwnd, IntPtr lParam)
    {
        // lParam 为屏幕物理像素坐标；窗口矩形/缩放换算全部走物理，规避 DPI 陷阱
        var x = (short)((long)lParam & 0xFFFF);
        var y = (short)(((long)lParam >> 16) & 0xFFFF);
        GetWindowRect(hwnd, out var rect);
        var scale = GetDpiScale(hwnd);

        var maximized = IsZoomed(hwnd);
        var resize = ResizeDip * scale;
        var caption = CaptionDip * scale;

        var inLeft = x - rect.Left < resize;
        var inRight = rect.Right - x < resize;
        var inTop = y - rect.Top < resize;
        var inBottom = rect.Bottom - y < resize;

        // 最大化时窗口贴满工作区，不提供边缘缩放
        if (!maximized)
        {
            if (inTop && inLeft) return (IntPtr)HtTopLeft;
            if (inTop && inRight) return (IntPtr)HtTopRight;
            if (inBottom && inLeft) return (IntPtr)HtBottomLeft;
            if (inBottom && inRight) return (IntPtr)HtBottomRight;
            if (inLeft) return (IntPtr)HtLeft;
            if (inRight) return (IntPtr)HtRight;
            if (inTop) return (IntPtr)HtTop;
            if (inBottom) return (IntPtr)HtBottom;
        }

        // 右上角按钮区放行给控件，其余标题栏区域交给系统（拖拽/双击/右键菜单/Snap）
        var relY = y - rect.Top;
        if (relY <= caption)
        {
            var inButtons = x > rect.Right - ButtonsZoneWidthDip * scale && relY <= ButtonsZoneHeightDip * scale;
            return inButtons ? (IntPtr)HtClient : (IntPtr)HtCaption;
        }
        return (IntPtr)HtClient;
    }

    private static double GetDpiScale(IntPtr hwnd)
    {
        var dc = GetDC(hwnd);
        try
        {
            var dpi = GetDeviceCaps(dc, Logpixelsx);
            return dpi <= 0 ? 1.0 : dpi / 96.0;
        }
        finally
        {
            ReleaseDC(hwnd, dc);
        }
    }

    private const int GwlStyle = -16;
    private const int WsThickframe = 0x00040000;
    private const int WsCaption = 0x00C00000;
    private const int WsSysmenu = 0x00080000;
    private const int WsMinimizebox = 0x00020000;
    private const int WsMaximizebox = 0x00010000;

    private const int WmNcCalcSize = 0x0083;
    private const int WmNcHitTest = 0x0084;
    private const int WmGetMinMaxInfo = 0x0024;

    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private const int Logpixelsx = 88;
    private const int MonitorDefaulttonearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, out MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr dc, int index);
}
