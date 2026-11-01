using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LesTool.Platform;
using Microsoft.Win32;
using Serilog;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 录制结果预览窗：播放器形态（进度条拖动/点击跳转、快进快退、倍速、逐帧、循环、
/// 双击全屏），看完决定保存还是丢弃。视频无音频轨，倍速无变调问题。
/// 临时目录由本窗负责清理。
/// </summary>
internal partial class RecordPreviewWindow : Window
{
    private static readonly double[] SpeedSteps = { 0.5, 1, 1.5, 2, 3, 4 };
    private static readonly TimeSpan FrameStep = TimeSpan.FromSeconds(1.0 / 30); // 4K30 录制
    private static readonly TimeSpan SkipStep = TimeSpan.FromSeconds(5);

    private readonly string _videoPath;
    private readonly string _sessionDir;
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private bool _finalized;
    private bool _opened;      // MediaOpened 前进度条不可用（总时长未知）
    private bool _dragging;    // 拖动中不回写进度条
    private bool _refreshing;  // 定时器回写进度条时屏蔽 ValueChanged 的回环 seek
    private bool _loop = true;
    private int _speedIndex = 1;

    internal RecordPreviewWindow(string videoPath, Rect regionPhys, TimeSpan duration, string sessionDir)
    {
        InitializeComponent();
        _videoPath = videoPath;
        _sessionDir = sessionDir;

        DurText.Text = Format(duration);
        Player.Source = new Uri(Path.GetFullPath(videoPath));
        // Manual 模式下设置 Source 不会开始加载，MediaOpened 也不会触发——
        // 必须显式 Play() 才打开媒体（日志证实：旧逻辑等 MediaOpened 再 Play，16 秒无任何媒体事件）
        Player.Loaded += (_, _) => Player.Play();
        BtnLoop.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x3B, 0x9E, 0xFF)); // 循环默认开

        _ticker.Tick += (_, _) => RefreshProgress();
        _ticker.Start();

        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        GdiCapture.MoveWindowPhys(helper.Handle, regionPhys); // 预览正好铺在原区域上
        Show();
        Activate(); // 拿焦点，快捷键/ESC 可用
        Closed += (_, _) => Cleanup();
    }

    #region 媒体事件

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        _opened = true;
        Seek.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds;
        DurText.Text = Format(Player.NaturalDuration.TimeSpan);
        Player.Play(); // 打开即播
        Log.Debug("录屏预览开始播放：{Path}", _videoPath);
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        if (_loop)
        {
            Player.Position = TimeSpan.Zero;
            Player.Play();
            return;
        }
        BtnPlayPause.Content = "播放";
        Log.Debug("录屏预览播放结束");
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Log.Error(e.ErrorException, "录屏预览打开失败：{Path}", _videoPath);
        ToastService.Show($"视频打开失败：{e.ErrorException.Message}", 4000);
        Close();
    }

    #endregion

    #region 播放控制

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (!_opened) return;
        if (Player.Position >= Player.NaturalDuration.TimeSpan)
            Player.Position = TimeSpan.Zero; // 非循环模式播完后按播放从头来
        if (BtnPlayPause.Content as string == "暂停")
        {
            Player.Pause();
            BtnPlayPause.Content = "播放";
            Log.Debug("录屏预览暂停 @{Pos}", Player.Position);
        }
        else
        {
            Player.Play();
            BtnPlayPause.Content = "暂停";
        }
    }

    private void OnSeekClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int seconds))
            SkipTo(Player.Position + TimeSpan.FromSeconds(seconds));
    }

    private void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        _speedIndex = (_speedIndex + 1) % SpeedSteps.Length;
        double speed = SpeedSteps[_speedIndex];
        Player.SpeedRatio = speed;
        BtnSpeed.Content = $"{speed:0.#}x";
        if (BtnPlayPause.Content as string != "暂停")
            Player.Play(); // 暂停中改倍速后恢复，避免暂停态倍速不生效的怪癖
        Log.Information("录屏预览倍速：{Speed}x", speed);
    }

    private void OnLoopClick(object sender, RoutedEventArgs e) => ToggleLoop();

    private void ToggleLoop()
    {
        _loop = !_loop;
        BtnLoop.Background = _loop ? new SolidColorBrush(Color.FromArgb(0x55, 0x3B, 0x9E, 0xFF)) : null;
        Log.Debug("录屏预览循环：{On}", _loop);
    }

    /// <summary>统一 seek 入口：钳制到 [0, 总时长]。</summary>
    private void SkipTo(TimeSpan position)
    {
        if (!_opened) return;
        var total = Player.NaturalDuration.TimeSpan;
        Player.Position = position < TimeSpan.Zero ? TimeSpan.Zero
                       : position > total ? total : position;
        RefreshProgress();
    }

    #endregion

    #region 进度条

    private void OnSeekDragStarted(object sender, DragStartedEventArgs e) => _dragging = true;

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        SkipTo(TimeSpan.FromSeconds(Seek.Value));
    }

    private void OnSeekValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 只处理用户点击轨道（IsMoveToPointEnabled）：拖动由 DragCompleted 收尾，定时器回写被 _refreshing 屏蔽
        if (_opened && !_dragging && !_refreshing)
            SkipTo(TimeSpan.FromSeconds(e.NewValue));
    }

    /// <summary>定时器：回写当前进度与时间文字（拖动中让位给用户）。</summary>
    private void RefreshProgress()
    {
        if (!_opened || _dragging) return;
        _refreshing = true;
        Seek.Value = Player.Position.TotalSeconds;
        _refreshing = false;
        PosText.Text = Format(Player.Position);
    }

    private static string Format(TimeSpan t)
    {
        t = t < TimeSpan.Zero ? TimeSpan.Zero : t;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    #endregion

    #region 键盘 / 全屏

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close(); // ESC = 丢弃
                break;
            case Key.Space:
                TogglePlay();
                break;
            case Key.Left:
                SkipTo(Player.Position - SkipStep);
                break;
            case Key.Right:
                SkipTo(Player.Position + SkipStep);
                break;
            case Key.OemComma: // 逐帧后退/前进（自动暂停）
            case Key.OemPeriod:
                if (_opened)
                {
                    Player.Pause();
                    BtnPlayPause.Content = "播放";
                    SkipTo(Player.Position + (e.Key == Key.OemPeriod ? FrameStep : -FrameStep));
                }
                break;
            case Key.L:
                ToggleLoop();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnVideoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
        }
    }

    #endregion

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LesTool");
        Directory.CreateDirectory(dir);

        var dialog = new SaveFileDialog
        {
            Title = "保存录屏",
            Filter = "MP4 视频|*.mp4",
            FileName = $"record-{DateTime.Now:yyyyMMdd-HHmmss}.mp4",
            InitialDirectory = dir,
        };
        if (dialog.ShowDialog(this) != true)
            return; // 取消则留在预览，可继续看或丢弃

        try
        {
            File.Copy(_videoPath, dialog.FileName, true);
            Log.Information("录屏已保存：{Path}", dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "录屏保存失败：{Path}", dialog.FileName);
            ToastService.Show($"保存失败：{ex.Message}，详见日志", 4000);
            return;
        }
        Close();
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>删除临时目录（保存前文件一直在这里，丢弃即清空）。</summary>
    private void Cleanup()
    {
        if (_finalized)
            return;
        _finalized = true;
        _ticker.Stop();
        try
        {
            Directory.Delete(_sessionDir, true);
            Log.Debug("录制临时目录已清理：{Dir}", _sessionDir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "录制临时目录清理失败：{Dir}", _sessionDir);
        }
    }
}
