using System.Diagnostics;
using System.IO;
using System.Windows;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 录屏控制器：采集与编码全部交给外置 ffmpeg——ddagrab（桌面复制，GPU 内完成）
/// 直接喂硬件编码器，全程不过 CPU、不占大内存。本类只负责参数拼装、进程生命周期、
/// 暂停分段与拼接、UI 窗口调度。
/// 暂停 = 向 ffmpeg 发 q 优雅收段；继续 = 新开一段；停止后多段用 concat 流拷贝无损拼接。
/// 全部方法须在 UI 线程调用。
/// </summary>
internal static class ScreenRecorder
{
    public enum RecState { Idle, Countdown, Recording, Paused, Finalizing }

    private const int Framerate = 30;

    // 硬件编码器直连 d3d11 帧（零拷贝）；hwdownload 为 CPU 兜底路径
    private sealed record EncoderSpec(string Encoder, string Options, bool GpuDirect);

    private static readonly EncoderSpec[] EncoderCandidates =
    {
        new("h264_nvenc", "-preset p4 -cq 23", true),
        new("h264_qsv", "-global_quality 23", true),
        new("h264_amf", "-quality balanced", true),
        new("h264_qsv", "-global_quality 23", false),
        new("h264_amf", "-quality balanced", false),
        new("libx264", "-preset veryfast -crf 23", false),
    };

    private static bool _shuttingDown;
    public static void Initialize() => _shuttingDown = false;

    private static RecState _state = RecState.Idle;
    private static Rect _regionPhys = Rect.Empty;  // 录制区域（全局物理像素）
    private static Rect _monitorPhys;              // 所在显示器（用于算屏内偏移）
    private static int _outputIdx;                 // ddagrab 的显示器序号
    private static string? _sessionDir;            // 本次录制的临时目录
    private static readonly List<string> _segments = new();
    private static string? _currentSegmentPath;
    private static Process? _ffmpeg;
    private static RecordFrameWindow? _frame;
    private static RecordBarWindow? _bar;
    private static EncoderSpec? _encoder;          // 探测一次后缓存
    private static readonly object _probeLock = new(); // 预热与录制首探并发时串行化

    private static TimeSpan _accumulated = TimeSpan.Zero; // 已完结段的总时长
    private static readonly Stopwatch _segmentWatch = new();

    public static RecState State => _state;
    public static bool IsBusy => _state != RecState.Idle;

    /// <summary>当前累计已录时长（暂停时冻结）。</summary>
    public static TimeSpan Elapsed =>
        _state is RecState.Recording ? _accumulated + _segmentWatch.Elapsed : _accumulated;

    /// <summary>开始一次录屏。region 为全局物理像素矩形（来自截图会话的选区）。</summary>
    public static void Begin(Rect region)
    {
        if (_shuttingDown || IsBusy)
        {
            Log.Warning("已有录制进行中，忽略新的录制请求");
            return;
        }

        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            Log.Warning("未找到 ffmpeg.exe，录屏不可用（搜索序：plugins/screenshot/assets → 程序目录）");
            ToastService.Show("未找到 ffmpeg.exe：放到 plugins/screenshot/assets/ 或程序目录后重试", 5000);
            return;
        }
        ResolveEncoder(ffmpeg); // 首次会跑一轮探测（约 1~3 秒），结果缓存

        // 选区 → 覆盖最多的显示器；跨屏选区裁到该屏内
        var monitors = GdiCapture.GetMonitorBounds();
        Rect bestMonitor = Rect.Empty;
        int bestIdx = -1;
        double bestArea = 0;
        for (int i = 0; i < monitors.Count; i++)
        {
            Rect inter = Rect.Intersect(region, monitors[i]);
            double area = inter.IsEmpty ? 0 : inter.Width * inter.Height;
            if (area > bestArea)
            {
                bestArea = area;
                bestMonitor = monitors[i];
                bestIdx = i;
            }
        }
        Rect clamped = Rect.Intersect(region, bestMonitor);
        if (bestIdx < 0 || clamped.IsEmpty || clamped.Width < 8 || clamped.Height < 8)
        {
            Log.Warning("录制区域无效：{Region}", region);
            return;
        }

        _regionPhys = clamped;
        _monitorPhys = bestMonitor;
        _outputIdx = bestIdx;
        _sessionDir = Path.Combine(Path.GetTempPath(), "LesTool", "rec", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sessionDir);
        _segments.Clear();
        _accumulated = TimeSpan.Zero;
        _currentSegmentPath = null;

        _state = RecState.Countdown;
        _frame = new RecordFrameWindow(_regionPhys);
        _frame.CountdownDone += OnCountdownDone;
        Log.Information("录屏准备：区域 {Region}（显示器 {Index}）", _regionPhys, _outputIdx);
    }

    private static void OnCountdownDone()
    {
        StartSegment();
        _bar = new RecordBarWindow(_regionPhys);
        Log.Information("录制开始：{Encoder}，30fps", _encoder!.Encoder);
    }

    #region 分段进程

    private static void StartSegment()
    {
        _currentSegmentPath = Path.Combine(_sessionDir!, $"seg{_segments.Count + 1:D3}.mp4");
        var psi = new ProcessStartInfo(FindFfmpeg()!, BuildSegmentArgs(_currentSegmentPath))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        _ffmpeg = Process.Start(psi);
        _ffmpeg!.EnableRaisingEvents = true;
        _ffmpeg.Exited += OnSegmentExited;
        _ffmpeg.BeginErrorReadLine(); // 排空 stderr 防管道堵塞（内容丢弃，出错时看不了细节但不会卡死）
        _segmentWatch.Restart();
        _state = RecState.Recording;
    }

    private static string BuildSegmentArgs(string outputPath)
    {
        var spec = _encoder!;
        var offset = new Point(_regionPhys.X - _monitorPhys.X, _regionPhys.Y - _monitorPhys.Y);
        string grab = $"ddagrab=output_idx={_outputIdx}:framerate={Framerate}" +
            $":offset_x={(int)Math.Round(offset.X)}:offset_y={(int)Math.Round(offset.Y)}" +
            $":video_size={(int)Math.Round(_regionPhys.Width)}x{(int)Math.Round(_regionPhys.Height)}";
        string filter = spec.GpuDirect ? "" : " -vf hwdownload,format=bgra";
        return $"-hide_banner -loglevel error -f lavfi -i {grab}{filter} " +
            $"-c:v {spec.Encoder} {spec.Options} -y \"{outputPath}\"";
    }

    /// <summary>优雅停止当前段（q 收尾保 MP4 容器完整），最多等 3 秒后强杀。</summary>
    private static void StopSegmentProcess()
    {
        if (_ffmpeg is not { } proc)
            return;
        try { proc.StandardInput.WriteLine("q"); }
        catch (Exception ex) { Log.Warning(ex, "向 ffmpeg 发送停止指令失败"); }

        if (!proc.WaitForExit(3000))
        {
            Log.Warning("ffmpeg 3 秒未退出，强制结束");
            try { proc.Kill(); } catch { /* 进程已死 */ }
        }
        SegmentFinished(proc, intentional: true);
    }

    private static void OnSegmentExited(object? sender, EventArgs e)
    {
        if (_shuttingDown) return;
        var proc = (Process)sender!;
        // Exited 在线程池线程回调，封送回 UI 线程
        Application.Current?.Dispatcher.BeginInvoke(() => SegmentFinished(proc, intentional: false));
    }

    /// <summary>段进程结束的统一处理。同步路径与 Exited 回调都可能进来，_ffmpeg 判重保证只跑一次。</summary>
    private static void SegmentFinished(Process proc, bool intentional)
    {
        if (_shuttingDown || _ffmpeg != proc)
            return; // 已被同步路径处理，或属于上一次会话
        _ffmpeg = null;
        _segmentWatch.Stop();

        try
        {
            if (_currentSegmentPath is { } path && File.Exists(path) && new FileInfo(path).Length > 0)
                _segments.Add(path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "检查段文件失败");
        }

        if (!intentional)
        {
            // 改分辨率/拔显示器等导致 ffmpeg 自己退出：按停止处理，保住已录内容
            Log.Error("ffmpeg 意外退出，自动收尾（已录内容保留）");
            if (_state == RecState.Recording)
                _accumulated += _segmentWatch.Elapsed;
            if (_state is RecState.Recording or RecState.Paused or RecState.Countdown)
                FinishRecording();
        }
    }

    #endregion

    #region 控制

    public static void Pause()
    {
        if (_state != RecState.Recording)
            return;
        _accumulated += _segmentWatch.Elapsed;
        _state = RecState.Paused;
        StopSegmentProcess();
        _frame?.SetPaused(true);
        Log.Debug("录制已暂停，累计 {Elapsed}", Elapsed);
    }

    public static void Resume()
    {
        if (_state != RecState.Paused)
            return;
        _frame?.SetPaused(false);
        StartSegment();
        Log.Debug("录制已继续");
    }

    public static void Stop()
    {
        if (_state is not (RecState.Recording or RecState.Paused))
            return;
        if (_state == RecState.Recording)
            _accumulated += _segmentWatch.Elapsed;
        _state = RecState.Finalizing;
        StopSegmentProcess();
        FinishRecording();
    }

    /// <summary>应用退出时调用：优雅停 ffmpeg，保留临时段文件供手工挽救。</summary>
    private static Process? BeginShutdown()
    {
        if (_shuttingDown) return null;
        _shuttingDown = true;
        var process = _ffmpeg; _ffmpeg = null;
        if (process is not null) process.Exited -= OnSegmentExited;
        if (_state != RecState.Idle && _sessionDir is not null)
            Log.Warning("退出时录制中断，片段保留于 {Dir}", _sessionDir);
        _state = RecState.Idle; _segmentWatch.Stop();
        if (_frame is not null) { _frame.CountdownDone -= OnCountdownDone; _frame.Close(); _frame = null; }
        _bar?.Close(); _bar = null;
        try { process?.StandardInput.WriteLine("q"); } catch (Exception error) { Log.Debug(error, "录制进程已退出"); }
        return process;
    }
    public static async Task ShutdownAsync()
    {
        var process = BeginShutdown(); if (process is null) return;
        using (process)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                try { process.Kill(); await process.WaitForExitAsync().ConfigureAwait(false); }
                catch (InvalidOperationException) { }
            }
        }
    }
    public static void Shutdown()
    {
        var process = BeginShutdown(); if (process is null) return;
        using (process)
        {
            if (!process.WaitForExit(2000))
                try { process.Kill(); process.WaitForExit(); } catch (InvalidOperationException) { }
        }
    }

    #endregion

    #region 收尾

    private static void FinishRecording()
    {
        _frame?.Close();
        _frame = null;
        _bar?.Close();
        _bar = null;

        string? sessionDir = _sessionDir;
        TimeSpan duration = _accumulated;
        List<string> segments = _segments.ToList();
        ResetState();

        try
        {
            string final = segments.Count switch
            {
                0 => throw new InvalidOperationException("没有任何有效录制内容"),
                1 => segments[0],
                _ => ConcatSegments(sessionDir!, segments),
            };
            Log.Information("录制完成：{Count} 段，总时长 {Duration:hh\\:mm\\:ss}",
                segments.Count, duration);
            // 临时目录移交给预览窗，由它负责保存后的清理
            new RecordPreviewWindow(final, _regionPhys, duration, sessionDir!);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "录制收尾失败");
            try { Directory.Delete(sessionDir!, true); } catch { /* 尽力而为 */ }
        }
    }

    /// <summary>多段无损拼接（concat + 流拷贝，秒级完成）。</summary>
    private static string ConcatSegments(string sessionDir, List<string> segments)
    {
        string listPath = Path.Combine(sessionDir, "list.txt");
        File.WriteAllLines(listPath, segments.Select(s =>
            $"file '{s.Replace('\\', '/').Replace("'", "'\\''")}'"));
        string output = Path.Combine(sessionDir, "final.mp4");

        var psi = new ProcessStartInfo(FindFfmpeg()!,
                $"-hide_banner -loglevel error -f concat -safe 0 -i \"{listPath}\" -c copy -y \"{output}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        _ = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(15000))
        {
            try { proc.Kill(); } catch { /* 已退出 */ }
            throw new TimeoutException("段拼接超时");
        }
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"段拼接失败，退出码 {proc.ExitCode}");
        return output;
    }

    private static void ResetState()
    {
        _state = RecState.Idle;
        _segments.Clear();
        _currentSegmentPath = null;
        _accumulated = TimeSpan.Zero;
        _segmentWatch.Reset();
        _frame = null;
        _bar = null;
        // _sessionDir 由预览窗接管清理；这里不清（失败路径在外层删）
    }

    #endregion

    #region ffmpeg 探测

    /// <summary>ffmpeg 搜索序：插件资产目录优先，程序目录（旧约定）向后兼容。</summary>
    private static string? FindFfmpeg()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "plugins", "screenshot", "assets", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>启动时后台预热编码器探测（约 1~3 秒的 ffmpeg 试编链），
    /// 用户点录制时编码器已就绪，避免 UI 线程卡顿。ffmpeg 缺失则静默跳过。</summary>
    public static void Warmup()
    {
        if (_encoder is not null) return;
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;
        Log.Debug("录屏编码器预热：后台探测开始");
        Task.Run(() =>
        {
            try { ResolveEncoder(ffmpeg); }
            catch (Exception ex) { Log.Error(ex, "录屏编码器预热失败，录制时将重试探测"); }
        });
    }

    /// <summary>编码器探测：用真实 ddagrab 链路各试 1 秒，可用即选定（结果静态缓存）。</summary>
    private static void ResolveEncoder(string ffmpeg)
    {
        if (_encoder is not null)
            return;
        lock (_probeLock)
        {
            if (_encoder is not null) // 等锁期间预热已完成
                return;
            foreach (EncoderSpec spec in EncoderCandidates)
            {
                string filter = spec.GpuDirect ? "" : " -vf hwdownload,format=bgra";
                string args = $"-hide_banner -loglevel error -t 1 -f lavfi -i ddagrab=output_idx=0:framerate=10{filter} " +
                    $"-c:v {spec.Encoder} {spec.Options} -f null -";
                if (RunProbe(ffmpeg, args) == 0)
                {
                    _encoder = spec;
                    Log.Information("录屏编码器选定：{Encoder}（GPU 直连={Direct}）", spec.Encoder, spec.GpuDirect);
                    return;
                }
                Log.Debug("编码器不可用，换下一个：{Encoder}（直连={Direct}）", spec.Encoder, spec.GpuDirect);
            }
            throw new InvalidOperationException("ffmpeg 没有任何可用的 H.264 编码器（含 libx264），请检查 ffmpeg 构建完整性");
        }
    }

    private static int RunProbe(string ffmpeg, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            _ = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(); } catch { /* 已退出 */ }
                return -1;
            }
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    #endregion
}
