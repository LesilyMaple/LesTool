using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace LesTool.Platform.Controls;

/// <summary>
/// 浮层面板窗去系统菜单（attached）：WPF 无边框窗默认仍带 WS_SYSMENU——面板热键
/// 默认 Alt+Space，热键触发后残留的 Alt 菜单模式会让面板内按 ↑↓ 弹出左上角
/// 系统菜单（还原/移动/大小/最小化/最大化/关闭，2026-10-21 实测）。挂本行为：
/// SourceInitialized 时剥掉 WS_SYSMENU/最小化/最大化样式位，并兜底吞掉
/// WM_SYSCOMMAND 的 SC_KEYMENU（键盘路径开系统菜单）。面板类浮窗不进任务栏、
/// 不可缩放，本就用不到系统菜单。
/// </summary>
public static class NoSystemMenu
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(NoSystemMenu), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool value) => o.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window w) return;
        w.SourceInitialized += (_, _) => Strip(w);
    }

    private static void Strip(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        var style = GetWindowLong(hwnd, GwlStyle);
        SetWindowLong(hwnd, GwlStyle, style & ~(WsSysmenu | WsMinimizebox | WsMaximizebox));
        HwndSource.FromHwnd(hwnd)?.AddHook(SwallowKeyMenu);
        Log.Debug("浮窗系统菜单已剥除：0x{Hwnd}", hwnd);
    }

    private static IntPtr SwallowKeyMenu(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSysCommand && (wParam.ToInt64() & 0xFFF0) == ScKeyMenu)
        {
            handled = true; // Alt 残留菜单模式的残余触发：键盘开系统菜单一律吞
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private const int GwlStyle = -16;
    private const int WsMaximizebox = 0x00010000;
    private const int WsMinimizebox = 0x00020000;
    private const int WsSysmenu = 0x00080000;

    private const int WmSysCommand = 0x0112;
    private const long ScKeyMenu = 0xF100;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
