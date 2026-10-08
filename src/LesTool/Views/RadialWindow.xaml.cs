using System.Windows;
using System.Windows.Input;

namespace LesTool.Views;

/// <summary>
/// 悬浮轮盘窗口（骨架占位视觉）。正式版：WS_EX_NOACTIVATE 等扩展样式、
/// 扇形 Path 命中测试与合成线程动画。TODO(DPI)：钩子坐标是物理像素，
/// 当前按主屏 100% 缩放处理，多屏/高 DPI 需换算。
/// </summary>
public partial class RadialWindow : Window
{
    public RadialWindow() => InitializeComponent();

    /// <summary>预创建 HWND 并完成一次布局渲染，把首次显示的开销移到启动期。内容全透明，用户不可见。</summary>
    public void PrepareForFastShow()
    {
        Left = -32000;
        Top = -32000;
        Show();
        Hide();
    }

    public void ShowAt(Point centerScreenPx)
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Clamp(centerScreenPx.X - ActualWidth / 2, workArea.Left, Math.Max(workArea.Left, workArea.Right - ActualWidth));
        Top = Math.Clamp(centerScreenPx.Y - ActualHeight / 2, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - ActualHeight));
        Show();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Hide();
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Hide();
}
