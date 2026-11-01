using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using LesTool.Platform;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 录制控制条：计时 + 暂停/继续 + 停止。贴录制区域下沿，放不下翻到上方。
/// 计时读 ScreenRecorder.Elapsed（暂停时冻结），边框颜色与暂停状态联动。
/// </summary>
internal partial class RecordBarWindow : Window
{
    private readonly Rect _regionPhys;
    private readonly System.Windows.Threading.DispatcherTimer _timer =
        new() { Interval = TimeSpan.FromMilliseconds(250) };

    internal RecordBarWindow(Rect regionPhys)
    {
        InitializeComponent();
        _regionPhys = regionPhys;

        // 先按估算尺寸粗摆（避免默认位置闪现），Loaded 后按实际尺寸精确摆
        double scale = GdiCapture.GetMonitorScale(
            new Point(regionPhys.X + regionPhys.Width / 2, regionPhys.Y + regionPhys.Height / 2));
        Left = (_regionPhys.X + _regionPhys.Width / 2) / scale - 120;
        Top = (_regionPhys.Bottom + 8) / scale;
        Loaded += (_, _) => PositionBar();

        _timer.Tick += (_, _) => Refresh();
        Show();
        _timer.Start();
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

    private void Refresh()
    {
        bool paused = ScreenRecorder.State == ScreenRecorder.RecState.Paused;
        TimeText.Text = ScreenRecorder.Elapsed >= TimeSpan.FromHours(1)
            ? ScreenRecorder.Elapsed.ToString(@"h\:mm\:ss")
            : ScreenRecorder.Elapsed.ToString(@"mm\:ss");
        BtnPause.ToolTip = paused ? "继续" : "暂停";
        RecDot.Fill = paused
            ? new SolidColorBrush(Color.FromRgb(0xF9, 0xA8, 0x25))
            : new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (ScreenRecorder.State == ScreenRecorder.RecState.Paused)
            ScreenRecorder.Resume();
        else
            ScreenRecorder.Pause();
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => ScreenRecorder.Stop();

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            ScreenRecorder.Stop();
            e.Handled = true;
        }
    }
}
