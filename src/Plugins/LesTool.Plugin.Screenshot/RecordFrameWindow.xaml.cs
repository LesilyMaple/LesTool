using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using LesTool.Platform;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 录制区域边框窗：窗口比录制区域大一圈（边框在外沿，成品不含边框），
/// 整窗 WS_EX_TRANSPARENT 点击穿透——录制期间鼠标事件全部到达桌面。
/// 倒计时大数字也画在这里，倒计时结束（数字隐藏）后 ffmpeg 才启动，不会入镜。
/// </summary>
internal partial class RecordFrameWindow : Window
{
    private static readonly Brush RecordingBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly Brush PausedBrush = new SolidColorBrush(Color.FromRgb(0xF9, 0xA8, 0x25));

    /// <summary>倒计时结束（数字已隐藏），可以开始录制。</summary>
    public event Action? CountdownDone;

    private int _count = 3;
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    internal RecordFrameWindow(Rect regionPhys)
    {
        InitializeComponent();

        // 物理定位：区域外扩 2 DIP 边框（换算成物理像素）
        double scale = GdiCapture.GetMonitorScale(
            new Point(regionPhys.X + regionPhys.Width / 2, regionPhys.Y + regionPhys.Height / 2));
        double b = 2 * scale;
        var bounds = new Rect(
            regionPhys.X - b, regionPhys.Y - b,
            regionPhys.Width + 2 * b, regionPhys.Height + 2 * b);

        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        GdiCapture.MoveWindowPhys(helper.Handle, bounds);
        SourceInitialized += (_, _) => MakeClickThrough(helper.Handle);

        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnCountdownTick;
        Show();
        _timer.Start();
    }

    /// <summary>暂停/恢复时切换边框颜色（红=录制中，黄=暂停）。</summary>
    public void SetPaused(bool paused)
    {
        Brush brush = paused ? PausedBrush : RecordingBrush;
        EdgeTop.Fill = brush;
        EdgeBottom.Fill = brush;
        EdgeLeft.Fill = brush;
        EdgeRight.Fill = brush;
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _count--;
        if (_count > 0)
        {
            Countdown.Text = _count.ToString();
            return;
        }
        _timer.Stop();
        Countdown.Visibility = Visibility.Collapsed; // 数字先隐藏，再启动 ffmpeg，避免入镜
        CountdownDone?.Invoke();
    }

    private static void MakeClickThrough(IntPtr hwnd)
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
