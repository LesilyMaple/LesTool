using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace LesTool.Platform;

/// <summary>
/// GDI 屏幕捕获与显示器信息。全部使用虚拟屏物理像素坐标系
/// （主显示器左上角为 0,0，左侧/上方显示器坐标为负）。
/// 平台共享工具：宿主与插件（截图等）共同消费，属 LesTool.Platform 契约面。
/// </summary>
public static class GdiCapture
{
    /// <summary>BitBlt 一次抓整块虚拟屏幕（所有显示器拼接）。
    /// 返回位图和它的物理像素边界（左上角通常是负数或 0）。</summary>
    public static (BitmapSource Bitmap, Rect Bounds) CaptureVirtualScreen()
    {
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"虚拟屏幕尺寸异常：{w}x{h}");

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr bitmap = CreateCompatibleBitmap(screenDc, w, h);
        IntPtr oldBitmap = SelectObject(memDc, bitmap);
        try
        {
            // CAPTUREBLT：连分层窗口（半透明/置顶窗）一起抓，否则这类窗口会缺失
            if (!BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException($"BitBlt 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return (source, new Rect(x, y, w, h));
        }
        finally
        {
            SelectObject(memDc, oldBitmap);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>抓取任意物理矩形区域（长截图连拍用）。不带 CAPTUREBLT：
    /// 连拍场景下省掉分层窗口合成开销，普通窗口内容不受影响。</summary>
    public static BitmapSource CaptureRegion(Rect phys)
    {
        int x = (int)Math.Round(phys.X), y = (int)Math.Round(phys.Y);
        int w = (int)Math.Round(phys.Width), h = (int)Math.Round(phys.Height);
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"抓屏区域非法：{phys}");

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr bitmap = CreateCompatibleBitmap(screenDc, w, h);
        IntPtr oldBitmap = SelectObject(memDc, bitmap);
        try
        {
            if (!BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY))
                throw new InvalidOperationException($"BitBlt 失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            SelectObject(memDc, oldBitmap);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>枚举所有显示器的物理像素边界。</summary>
    public static List<Rect> GetMonitorBounds()
    {
        var result = new List<Rect>();
        bool Callback(IntPtr hMonitor, IntPtr hdc, ref RECT rc, IntPtr data)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var m = info.rcMonitor;
                result.Add(new Rect(m.left, m.top, m.right - m.left, m.bottom - m.top));
            }
            return true;
        }
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return result;
    }

    /// <summary>指定物理坐标所在显示器的 DPI 缩放比（PerMonitorV2 下各屏不同）。</summary>
    public static double GetMonitorScale(Point phys)
    {
        IntPtr monitor = MonitorFromPoint(ToPoint(phys), MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE, out uint dpiX, out _) == 0)
            return dpiX / 96.0;
        return 1.0;
    }

    /// <summary>指定物理坐标所在显示器的工作区（物理像素）。失败返回 Empty。</summary>
    public static Rect GetMonitorWorkArea(Point phys)
    {
        IntPtr monitor = MonitorFromPoint(ToPoint(phys), MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                RECT w = info.rcWork;
                return new Rect(w.left, w.top, w.right - w.left, w.bottom - w.top);
            }
        }
        return Rect.Empty;
    }

    /// <summary>用物理像素直接摆窗口（不抢焦点、不改 Z 序）。
    /// PerMonitorV2 下 WPF 的 Left/Top 用的还是窗口"当前"屏的 DPI，
    /// 跨屏定位会错位，物理坐标直摆是唯一可靠方式。</summary>
    public static void MoveWindowPhys(IntPtr hwnd, Rect phys)
    {
        SetWindowPos(hwnd, IntPtr.Zero,
            (int)Math.Round(phys.X), (int)Math.Round(phys.Y),
            (int)Math.Round(phys.Width), (int)Math.Round(phys.Height),
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>当前光标的物理像素坐标。</summary>
    public static Point GetCursorPosition()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    private static POINT ToPoint(Point p) => new() { X = (int)p.X, Y = (int)p.Y };

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE = 0;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr destDc, int destX, int destY, int width, int height,
        IntPtr srcDc, int srcX, int srcY, uint rop);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
