using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LesTool.Platform;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 长截图控制条：实时缩略预览 + 状态文字 + 完成/取消。贴捕获区域下方，放不下翻上方。
/// </summary>
internal partial class LongBarWindow : Window
{
    private readonly Rect _regionPhys;

    internal LongBarWindow(Rect regionPhys)
    {
        InitializeComponent();
        _regionPhys = regionPhys;

        double scale = GdiCapture.GetMonitorScale(
            new Point(regionPhys.X + regionPhys.Width / 2, regionPhys.Y + regionPhys.Height / 2));
        Left = (_regionPhys.X + _regionPhys.Width / 2) / scale - 160;
        Top = (_regionPhys.Bottom + 8) / scale;
        Loaded += (_, _) => PositionBar();

        Show();
    }

    private void PositionBar()
    {
        Matrix? m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        double scale = m?.M11 ?? 1.0;
        double w = ActualWidth * scale;
        double h = ActualHeight * scale;
        Rect work = GdiCapture.GetMonitorWorkArea(
            new Point(_regionPhys.X + _regionPhys.Width / 2, _regionPhys.Y + _regionPhys.Height / 2));

        double x = _regionPhys.X;
        double y = _regionPhys.Bottom + 8 * scale;
        if (!work.IsEmpty)
        {
            x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
            if (y + h > work.Bottom)
                y = _regionPhys.Top - h - 8 * scale;
            if (y < work.Top)
                y = work.Top;
        }
        GdiCapture.MoveWindowPhys(new WindowInteropHelper(this).Handle, new Rect(x, y, w, h));
    }

    /// <summary>刷新状态文字。</summary>
    public void UpdateStatus(string text) => StatusText.Text = text;

    private void OnFinishClick(object sender, RoutedEventArgs e) => LongScreenshot.Finish();

    private void OnCancelClick(object sender, RoutedEventArgs e) => LongScreenshot.Cancel();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            LongScreenshot.Cancel();
            e.Handled = true;
        }
    }
}
