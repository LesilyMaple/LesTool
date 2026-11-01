using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LesTool.Platform;
using Microsoft.Win32;
using Serilog;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 框选截图入口：冻结全屏 → 每显示器一块遮罩窗 → SelectionSession 交互
/// → 回车确认时从冻结图裁剪，写剪贴板（2026-10-14 拍板：不自动落盘，存文件走「另存」）。
/// PerMonitorV2：遮罩按各屏自己的 DPI 换算，混合缩放多显示器下也不偏移。
/// </summary>
internal sealed class ScreenshotService
{
    private static ScreenshotService? _active;
    private static bool _stopping;
    internal static void Initialize() => _stopping = false;
    internal static void Shutdown()
    {
        _stopping = true;
        _active?.End();
        if (Application.Current is { } app)
            foreach (var window in app.Windows.Cast<Window>().Where(w => w.GetType().Assembly == typeof(ScreenshotService).Assembly && w is not RecordFrameWindow && w is not RecordBarWindow).ToArray())
                try { window.Close(); } catch (Exception error) { Log.Debug(error, "截图窗口关闭失败"); }
    }

    private BitmapSource? _frozen;        // 整块虚拟屏冻结图
    private Rect _virtualBounds;          // 冻结图的物理像素边界
    private SelectionSession? _session;
    private AnnotationState? _annotations;
    private List<OverlayWindow>? _overlays;
    private IDisposable? _radialScope;    // 会话期轮盘抑制（RadialGuard 作用域，End 释放）
    private bool _qrBusy;                 // 二维码识别重入闸（解码器自身无状态，闸按会话挡）

    /// <summary>启动一次框选截图。会话进行中重复调用直接忽略。
    /// 先占 _active 再 Begin：Begin 半途失败由 End 幂等收摊（遮罩/抑制不残留）。</summary>
    public static void StartInteractiveCapture()
    {
        if (_stopping || _active is not null) return;
        var service = new ScreenshotService();
        _active = service;
        try
        {
            service.Begin();
        }
        catch
        {
            service.End(); // 幂等：收掉半开的遮罩与轮盘抑制，再抛给调用方
            throw;
        }
    }

    private void Begin()
    {
        _radialScope = RadialGuard.BeginSuppress();

        (_frozen, _virtualBounds) = GdiCapture.CaptureVirtualScreen();

        _session = new SelectionSession(_virtualBounds);
        _session.Confirmed += OnConfirmed;
        _session.Pinned += OnPinned;
        _session.SaveRequested += OnSaveRequested;
        _session.RecordRequested += OnRecordRequested;
        _session.OcrRequested += OnOcrRequested;
        _session.QrRequested += OnQrRequested;
        _session.LongShotRequested += OnLongShotRequested;
        _session.Cancelled += OnCancelled;
        _annotations = new AnnotationState();

        var monitors = GdiCapture.GetMonitorBounds();
        if (monitors.Count == 0)
            throw new InvalidOperationException("未找到任何显示器");

        _overlays = new List<OverlayWindow>();
        foreach (Rect monitor in monitors)
        {
            var crop = new CroppedBitmap(_frozen, new Int32Rect(
                (int)(monitor.X - _virtualBounds.X), (int)(monitor.Y - _virtualBounds.Y),
                (int)monitor.Width, (int)monitor.Height));
            crop.Freeze();
            _overlays.Add(new OverlayWindow(crop, monitor, _session, _annotations, MakeMosaic));
        }
        // 悬停检测只排除遮罩自己（按 HWND），本应用主窗口仍可被识别框选
        var excluded = _overlays.Select(o => o.Hwnd).ToHashSet();
        foreach (OverlayWindow overlay in _overlays)
        {
            overlay.ExcludeHandles = excluded;
            overlay.ShowAtPhysicalBounds();
        }

        // 焦点给光标所在的遮罩，键盘（ESC/回车）直接可用
        Point cursor = GdiCapture.GetCursorPosition();
        _overlays.FirstOrDefault(o => o.MonitorPhys.Contains(cursor))?.Activate();

        Log.Information("框选截图会话启动：{Count} 个遮罩，虚拟屏 {Bounds}", _overlays.Count, _virtualBounds);
    }

    /// <summary>裁剪选区并合成标注，得到最终图像（确认与钉图共用）。</summary>
    private BitmapSource BuildFinalImage(Rect selection)
    {
        var crop = new CroppedBitmap(_frozen, new Int32Rect(
            (int)(selection.X - _virtualBounds.X), (int)(selection.Y - _virtualBounds.Y),
            (int)selection.Width, (int)selection.Height));
        crop.Freeze();

        // 有标注时把标注层合成到裁剪图上（与预览共用 AnnotationPainter，所见即所得）
        if (_annotations is { HasItems: true })
        {
            int w = (int)selection.Width, h = (int)selection.Height;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawImage(crop, new Rect(0, 0, w, h));
                AnnotationPainter.Draw(dc, _annotations.Items, new Size(w, h));
            }
            var composite = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            composite.Render(visual);
            composite.Freeze();
            return composite;
        }
        return crop;
    }

    private void OnConfirmed(Rect selection)
    {
        int annotationCount = _annotations?.Items.Count ?? 0;
        BitmapSource final;
        try
        {
            final = BuildFinalImage(selection);
        }
        catch (Exception ex)
        {
            Abort(ex, "合成截图失败");
            return;
        }

        // 只写剪贴板（2026-10-14 拍板：回车确认不自动落盘，要存文件走「另存」）
        End();
        try
        {
            ClipboardService.MarkSelfWrite();
            Clipboard.SetImage(final);
            Log.Information("框选截图完成：{Width}x{Height}，标注 {Count} 条（已入剪贴板）",
                (int)selection.Width, (int)selection.Height, annotationCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "写剪贴板失败（会话已正常结束）");
            ToastService.Show("写入剪贴板失败，详见日志", 4000);
        }
    }

    private void OnPinned(Rect selection)
    {
        int annotationCount = _annotations?.Items.Count ?? 0;
        BitmapSource final;
        try
        {
            final = BuildFinalImage(selection);
        }
        catch (Exception ex)
        {
            Abort(ex, "合成钉图失败");
            return;
        }
        End(); // 先收遮罩，钉图出现在干净的桌面上
        try
        {
            new PinWindow(final, selection);
            Log.Information("钉图完成：选区 {Width}x{Height}，标注 {Count} 条",
                (int)selection.Width, (int)selection.Height, annotationCount);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开钉图窗口失败（会话已正常结束）");
            ToastService.Show("钉图打开失败，详见日志", 4000);
        }
    }

    /// <summary>对选区录屏：收起遮罩（桌面恢复实时），把区域交给录屏控制器。</summary>
    private void OnRecordRequested(Rect selection)
    {
        End();
        try
        {
            ScreenRecorder.Begin(selection);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动录屏失败");
            ToastService.Show("录屏启动失败，详见日志", 4000);
        }
    }

    /// <summary>对选区长截图：收起遮罩，区域交给长截图控制器。</summary>
    private void OnLongShotRequested(Rect selection)
    {
        End();
        try
        {
            LongScreenshot.Begin(selection);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动长截图失败");
            ToastService.Show("长截图启动失败，详见日志", 4000);
        }
    }

    /// <summary>对选区做文字识别：用干净裁剪图（不含标注），结果接在选区右侧显示。</summary>
    private async void OnOcrRequested(Rect selection)
    {
        try
        {
            var crop = new CroppedBitmap(_frozen, new Int32Rect(
                (int)(selection.X - _virtualBounds.X), (int)(selection.Y - _virtualBounds.Y),
                (int)selection.Width, (int)selection.Height));
            crop.Freeze();

            List<ScreenshotOcr.OcrLine>? lines = await ScreenshotOcr.RunAsync(crop);
            if (lines is null)
            {
                Log.Debug("OCR 忙碌中，忽略本次请求");
                return;
            }
            // 识别期间会话已结束或选区变了，结果不再有效
            if (_stopping || _session is not { Current: SelectionSession.State.Selected } ||
                _session.Selection != selection)
            {
                Log.Debug("OCR 完成但选区已变化，丢弃结果");
                return;
            }
            Log.Information("OCR 完成：选区 {Width}x{Height}，{Lines} 行",
                (int)selection.Width, (int)selection.Height, lines.Count);
            // QQ 式收尾：独立结果窗接管（左原图右文本），遮罩收摊恢复桌面
            var result = new OcrResultWindow(crop, lines, selection);
            result.Show();
            End();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OCR 失败");
            if (_session is { Current: SelectionSession.State.Selected })
                ToastService.Show($"识别失败：{ex.Message}", 4000);
        }
    }

    /// <summary>对选区识别二维码：用干净裁剪图（不含标注）。
    /// 识别到：弹结果卡并收摊；未识别到：Toast 提示且会话保留——失败多半是
    /// 没框准（框小切掉定位角/框大带进背景），保留选区调整后直接重试，
    /// 不必从头再截（与 OCR 恒开结果窗不同）。</summary>
    private async void OnQrRequested(Rect selection)
    {
        if (_qrBusy)
        {
            Log.Debug("二维码识别忙碌中，忽略本次请求");
            return;
        }
        _qrBusy = true;
        try
        {
            var crop = new CroppedBitmap(_frozen, new Int32Rect(
                (int)(selection.X - _virtualBounds.X), (int)(selection.Y - _virtualBounds.Y),
                (int)selection.Width, (int)selection.Height));
            crop.Freeze();

            QrDecoder.QrResult? qr = await QrDecoder.RunAsync(crop);

            // 识别期间会话已结束或选区变了，结果不再有效
            if (_session is not { Current: SelectionSession.State.Selected } ||
                _session.Selection != selection)
            {
                Log.Debug("二维码识别完成但选区已变化，丢弃结果");
                return;
            }

            if (qr is null)
            {
                Log.Information("二维码未识别到：选区 {Width}x{Height}，会话保留待调整重试",
                    (int)selection.Width, (int)selection.Height);
                ToastService.Show("未识别到二维码，可调整选区后重试", 4000);
                return;
            }

            Log.Information("二维码识别成功：{Kind}，{Chars} 字符",
                QrDecoder.IsHttpUrl(qr.Text) ? "链接" : "文本", qr.Text.Length);
            new QrResultWindow(qr, selection).Show();
            End(); // 结果卡接管，遮罩收摊恢复桌面（与 OCR 同节奏）
        }
        catch (Exception ex)
        {
            Log.Error(ex, "二维码识别失败");
            if (_session is { Current: SelectionSession.State.Selected })
                ToastService.Show($"识别失败：{ex.Message}", 4000);
        }
        finally
        {
            _qrBusy = false;
        }
    }

    private void OnSaveRequested(Rect selection)
    {
        // 对话框要挂在活动遮罩下面，否则会被置顶遮罩盖住
        Window? owner = _overlays?.FirstOrDefault(o => o.IsActive)
            ?? _overlays?.FirstOrDefault(o => o.MonitorPhys.Contains(GdiCapture.GetCursorPosition()));

        var dialog = new SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
            FileName = $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}",
            InitialDirectory = GetDefaultSaveDir(),
        };
        if (dialog.ShowDialog(owner) != true)
        {
            Log.Debug("已取消另存为，回到截图会话");
            return; // 取消不结束会话，选区还在
        }

        int annotationCount = _annotations?.Items.Count ?? 0;
        BitmapSource final;
        try
        {
            final = BuildFinalImage(selection);
        }
        catch (Exception ex)
        {
            Abort(ex, "合成截图失败");
            return;
        }

        End(); // 先收摊后写盘：写盘再失败也会话已复位
        try
        {
            var encoder = dialog.FilterIndex == 2
                ? new JpegBitmapEncoder { QualityLevel = 90 }
                : (BitmapEncoder)new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(final));
            using (var stream = File.Create(dialog.FileName))
                encoder.Save(stream);

            Log.Information("截图已另存：{Path}（{Width}x{Height}，标注 {Count} 条，格式 {Format}）",
                dialog.FileName, (int)selection.Width, (int)selection.Height, annotationCount,
                dialog.FilterIndex == 2 ? "JPEG" : "PNG");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "截图另存失败（会话已正常结束）");
            ToastService.Show("另存失败，详见日志", 4000);
        }
    }

    private static string GetDefaultSaveDir()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LesTool");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void OnCancelled()
    {
        Log.Debug("框选截图已取消");
        End();
    }

    /// <summary>会话内任何阶段失败的统一出口：记日志 + 提示 + 收摊。</summary>
    private void Abort(Exception ex, string what)
    {
        Log.Error(ex, "{What}，截图会话中止", what);
        ToastService.Show($"{what}，本次截图已取消", 4000);
        End();
    }

    /// <summary>收摊。幂等：异常兜底路径可能重复触达，重复调用安全；
    /// 单个遮罩关闭失败不阻断其余清理。</summary>
    private void End()
    {
        if (_overlays != null)
        {
            foreach (OverlayWindow overlay in _overlays)
            {
                try
                {
                    overlay.Close();
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "遮罩关闭失败（继续收其余）");
                }
            }
        }
        _overlays = null;
        _frozen = null;
        _session = null;
        _annotations = null;
        if (_active == this)
            _active = null;
        try
        {
            ScreenshotOcr.Release(); // 引擎随会话释放，平时零内存占用
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "OCR 引擎释放失败（不影响会话复位）");
        }
        _radialScope?.Dispose();
        _radialScope = null;
    }

    /// <summary>把指定物理矩形区域做成块状马赛克（缩小取平均 + 邻近放大成块）。</summary>
    private BitmapSource MakeMosaic(Rect regionPhys)
    {
        if (_frozen is not { } frozen)
            throw new InvalidOperationException("冻结图已释放");

        // 像素取整并钳回位图边界（拖拽可能带出半个像素）
        int x = Math.Clamp((int)Math.Round(regionPhys.X - _virtualBounds.X), 0, frozen.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Round(regionPhys.Y - _virtualBounds.Y), 0, frozen.PixelHeight - 1);
        int w = Math.Clamp((int)Math.Round(regionPhys.Width), 1, frozen.PixelWidth - x);
        int h = Math.Clamp((int)Math.Round(regionPhys.Height), 1, frozen.PixelHeight - y);

        const int block = 10;
        var crop = new CroppedBitmap(frozen, new Int32Rect(x, y, w, h));
        crop.Freeze();
        BitmapSource small = RenderScaled(crop, Math.Max(1, w / block), Math.Max(1, h / block),
            BitmapScalingMode.Linear); // 缩小 = 块内取平均
        BitmapSource mosaic = RenderScaled(small, w, h, BitmapScalingMode.NearestNeighbor); // 放大 = 方块感
        return mosaic;
    }

    private static BitmapSource RenderScaled(BitmapSource source, int width, int height, BitmapScalingMode mode)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, mode);
        using (DrawingContext dc = visual.RenderOpen())
            dc.DrawImage(source, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
