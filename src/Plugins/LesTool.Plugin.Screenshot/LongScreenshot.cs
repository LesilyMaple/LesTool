using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 长截图控制器（手动滚动版，QQ 同款交互）：
/// 滚轮驱动 + 画面静止收帧——区域内的滚轮事件启动静止检测，
/// 每 30ms 抓帧比对，连续两拍不变即"滚动动画停稳"，收帧进拼接器。
/// 拼接断裂（跳跃过大）提示回滚补齐；连续无新增提示可能到底。
/// 全部方法须在 UI 线程调用。
/// </summary>
internal static class LongScreenshot
{
    private enum State { Idle, Capturing }

    private const int SettleIntervalMs = 30;   // 静止检测采样间隔
    private const int SettleStableCount = 2;   // 连续不变的拍数 → 判定静止
    private const int SettleTimeoutTicks = 66; // 2 秒兜底：动画停不下来就取最新帧
    private const int BottomHintStreak = 3;    // 连续无新增的次数 → 提示到底

    private static State _state = State.Idle;
    private static Rect _region;
    private static readonly VerticalStitcher _stitcher = new();
    private static LongFrameWindow? _frame;
    private static LongBarWindow? _bar;
    private static DispatcherTimer? _settleTimer;
    private static byte[]? _lastSample;
    private static byte[]? _stableFrame;
    private static int _stableCount;
    private static int _settleTicks;
    private static int _noNewStreak;
    private static int _frameW, _frameH, _frameStride;

    public static bool IsBusy => _state != State.Idle;

    /// <summary>开始一次长截图。region 为全局物理像素矩形（截图会话的选区）。</summary>
    public static void Begin(Rect region)
    {
        if (IsBusy)
        {
            Log.Warning("已有长截图进行中，忽略新请求");
            return;
        }
        if (region.Width < 32 || region.Height < 64)
        {
            Log.Warning("长截图区域太小：{Region}", region);
            return;
        }

        _region = region;
        _noNewStreak = 0;

        // 首帧
        byte[]? first = CaptureFrameBytes();
        if (first is null)
        {
            Log.Error("长截图首帧抓取失败");
            return;
        }
        _stitcher.AddFirst(first, _frameW, _frameH);

        _frame = new LongFrameWindow(_region);
        _bar = new LongBarWindow(_region);
        _bar.UpdateStatus($"已捕获 {_stitcher.Height}px");

        MouseEvents.Wheel += OnWheel;
        _state = State.Capturing;
        Log.Information("长截图开始：区域 {Region}", _region);
    }

    #region 滚轮 → 静止收帧

    private static void OnWheel(System.Windows.Point cursorPhys, int delta)
    {
        // MouseEvents 枢纽已封送 UI 线程
        if (_state != State.Capturing || !IsFinite(cursorPhys))
            return;
        if (!_region.Contains(cursorPhys))
            return; // 只响应区域内的滚动
        StartSettleWatch();
    }

    private static bool IsFinite(System.Windows.Point p) =>
        !double.IsNaN(p.X) && !double.IsNaN(p.Y);

    private static void StartSettleWatch()
    {
        _stableCount = 0;
        _settleTicks = 0;
        _lastSample = CaptureFrameBytes();
        _settleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SettleIntervalMs) };
        if (!_settleTimer.IsEnabled)
        {
            _settleTimer.Tick -= OnSettleTick; // 防重复订阅
            _settleTimer.Tick += OnSettleTick;
            _settleTimer.Start();
        }
    }

    private static void OnSettleTick(object? sender, EventArgs e)
    {
        _settleTicks++;
        byte[]? sample = CaptureFrameBytes();
        if (sample is null)
            return;

        bool unchanged = _lastSample is not null &&
            sample.Length == _lastSample.Length &&
            sample.AsSpan().SequenceEqual(_lastSample);
        if (unchanged)
        {
            _stableCount++;
            _stableFrame = sample;
        }
        else
        {
            _stableCount = 0;
            _lastSample = sample;
        }

        if (_stableCount >= SettleStableCount || _settleTicks >= SettleTimeoutTicks)
        {
            _settleTimer!.Stop();
            ProcessStableFrame(_stableFrame ?? _lastSample);
        }
    }

    private static void ProcessStableFrame(byte[]? frame)
    {
        if (frame is null)
            return;
        var result = _stitcher.TryAppend(frame, _frameH);
        switch (result.Status)
        {
            case VerticalStitcher.AppendStatus.Appended:
                _noNewStreak = 0;
                _bar?.UpdateStatus($"已捕获 {_stitcher.Height}px");
                Log.Debug("长图追加 {Rows} 行（diff={Diff:F2}）", result.NewRows, result.BestDiff);
                break;

            case VerticalStitcher.AppendStatus.NoNewContent:
                _noNewStreak++;
                _bar?.UpdateStatus(_noNewStreak >= BottomHintStreak
                    ? $"已捕获 {_stitcher.Height}px · 可能已到底部，点完成结束"
                    : $"已捕获 {_stitcher.Height}px");
                break;

            case VerticalStitcher.AppendStatus.NoMatch:
                _noNewStreak = 0;
                _bar?.UpdateStatus("内容跳跃过大，请往回滚一点再继续");
                Log.Debug("长图匹配失败（diff={Diff:F2}），等待回滚", result.BestDiff);
                break;
        }
    }

    #endregion

    #region 结束

    public static void Finish()
    {
        if (_state != State.Capturing)
            return;
        _settleTimer?.Stop();

        byte[] pixels = _stitcher.Snapshot();
        var image = BitmapSource.Create(_frameW, _stitcher.Height, 96, 96,
            PixelFormats.Bgra32, null, pixels, _frameStride);
        image.Freeze();

        Teardown();
        new LongPreviewWindow(image, _region);
        Log.Information("长截图完成：{Width}x{Height}", _frameW, _stitcher.Height);
        ResetCore();
    }

    public static void Cancel()
    {
        if (_state != State.Capturing)
            return;
        _settleTimer?.Stop();
        Teardown();
        Log.Debug("长截图已取消");
        ResetCore();
    }

    /// <summary>应用退出时调用。</summary>
    public static void Shutdown() => Cancel();

    private static void Teardown()
    {
        MouseEvents.Wheel -= OnWheel;
        _frame?.Close();
        _frame = null;
        _bar?.Close();
        _bar = null;
    }

    private static void ResetCore()
    {
        _state = State.Idle;
        _lastSample = null;
        _stableFrame = null;
        _noNewStreak = 0;
    }

    #endregion

    /// <summary>抓当前区域帧并转 BGRA 字节（失败返回 null）。</summary>
    private static byte[]? CaptureFrameBytes()
    {
        try
        {
            var bitmap = new FormatConvertedBitmap(
                GdiCapture.CaptureRegion(_region), PixelFormats.Bgra32, null, 0);
            _frameW = bitmap.PixelWidth;
            _frameH = bitmap.PixelHeight;
            _frameStride = _frameW * 4;
            var buffer = new byte[_frameStride * _frameH];
            bitmap.CopyPixels(buffer, _frameStride, 0);
            return buffer;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "长截图抓帧失败");
            return null;
        }
    }
}
