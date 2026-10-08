using System.Windows;
using System.Windows.Media;

namespace LesTool.Views;

/// <summary>
/// 悬浮轮盘窗口。无边框 + 全透明 + 不抢焦点 + 置顶、不在任务栏出现。
/// TODO: 扇形几何命中、展开动画、WS_EX_NOACTIVATE 扩展样式、多屏 DPI。
/// </summary>
public partial class RadialWindow : Window
{
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    public RadialWindow() => InitializeComponent();

    /// <summary>
    /// 预创建 HWND 并完成一次布局渲染，把首次显示的开销移到启动期。
    /// 窗口定位在屏幕外且内容全透明，用户不可见。
    /// </summary>
    public void PrepareForFastShow()
    {
        Left = -32000;
        Top = -32000;
        Show();

        // 缓存主屏 DPI：钩子坐标是物理像素，WPF Left/Top 是 DIP，需要换算
        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiScaleX = dpi.PixelsPerInchX / 96.0;
        _dpiScaleY = dpi.PixelsPerInchY / 96.0;

        Hide();
    }

    /// <summary>在屏幕物理像素坐标 centerScreenPx 处居中显示。</summary>
    public void ShowAt(Point centerScreenPx)
    {
        var workArea = SystemParameters.WorkArea; // DIP 单位
        var x = centerScreenPx.X / _dpiScaleX;
        var y = centerScreenPx.Y / _dpiScaleY;

        Left = Math.Clamp(x - ActualWidth / 2, workArea.Left, Math.Max(workArea.Left, workArea.Right - ActualWidth));
        Top = Math.Clamp(y - ActualHeight / 2, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - ActualHeight));
        Show();
    }
}
