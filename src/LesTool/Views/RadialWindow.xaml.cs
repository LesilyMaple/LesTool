using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LesTool.Platform;

namespace LesTool.Views;

/// <summary>
/// 悬浮轮盘窗口。无边框 + 全透明 + 不抢焦点 + 置顶、不在任务栏出现。
/// 扩展样式加 WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW：永不成为活动窗口，
/// Alt+F4 无法误关、不进 Alt+Tab（窗口被关后由控制器自愈重建兜底）。
/// 定位用物理像素直摆（钩子坐标是物理像素）；PerMonitorV2 下跨屏显示
/// 会触发 DPI 变化，窗口被 WPF 重缩放后重新对心。
/// TODO: 扇形几何命中、展开动画。
/// </summary>
public partial class RadialWindow : Window
{
    private Point _centerPx; // 最近一次显示的物理中心

    public RadialWindow()
    {
        InitializeComponent();
        DpiChanged += (_, _) =>
        {
            if (IsVisible)
                PositionAtPhysicalCenter();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// 预创建 HWND 并完成一次布局渲染，把首次显示的开销移到启动期。
    /// 窗口内容全透明，短暂 Show 用户不可见。
    /// </summary>
    public void PrepareForFastShow()
    {
        Show();
        Hide();
    }

    /// <summary>在屏幕物理像素坐标 centerScreenPx 处居中显示。</summary>
    public void ShowAt(Point centerScreenPx)
    {
        _centerPx = centerScreenPx;
        PositionAtPhysicalCenter();
        Show();
    }

    /// <summary>按所在显示器 DPI 与工作区，把窗口中心对到 _centerPx（物理像素）。</summary>
    private void PositionAtPhysicalCenter()
    {
        if (!IsLoaded) return; // 预热阶段无布局尺寸

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        double scale = GdiCapture.GetMonitorScale(_centerPx);
        Rect work = GdiCapture.GetMonitorWorkArea(_centerPx);

        double w = ActualWidth * scale;
        double h = ActualHeight * scale;
        double x, y;
        if (work.IsEmpty)
        {
            x = _centerPx.X - w / 2;
            y = _centerPx.Y - h / 2;
        }
        else
        {
            x = Math.Clamp(_centerPx.X - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
            y = Math.Clamp(_centerPx.Y - h / 2, work.Top, Math.Max(work.Top, work.Bottom - h));
        }
        GdiCapture.MoveWindowPhys(hwnd, new Rect(x, y, w, h));
    }

    #region P/Invoke

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);

    #endregion
}
