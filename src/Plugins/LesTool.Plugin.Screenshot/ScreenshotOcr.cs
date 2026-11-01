using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RapidOcrNet;
using Serilog;
using SkiaSharp;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// RapidOCR（PP-OCRv5 中文）封装。懒加载：引擎在第一次识别时才创建
/// （加载模型约半秒）；会话内缓存复用；截图会话结束调 Release() 释放，
/// 平时软件挂着零额外内存。全部从 UI 线程调用，推理在线程池。
/// 模型是插件资产：优先 plugins/screenshot/assets/models/v5/，
/// 兼容旧布局（程序目录 models/v5/）。
/// </summary>
internal static class ScreenshotOcr
{
    private static RapidOcr? _engine;
    private static bool _running;

    /// <summary>一行识别结果：文本 + 在原图（物理像素）中的包围盒。</summary>
    public sealed record OcrLine(string Text, Rect Box);

    /// <summary>
    /// 识别图中的文字，返回逐行结果（无文字返回空列表）。
    /// 有识别正在进行时返回 null（调用方忽略本次点击）。
    /// </summary>
    public static async Task<List<OcrLine>?> RunAsync(BitmapSource image)
    {
        if (_running)
            return null;
        _running = true;
        try
        {
            if (_engine is null)
            {
                _engine = await Task.Run(CreateEngine);
                Log.Debug("OCR 引擎已加载（会话内缓存）");
            }

            // BitmapSource → SKBitmap：统一转 Bgra32 后整幅直拷，指针固定给推理用
            var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int w = source.PixelWidth, h = source.PixelHeight;
            var buffer = new byte[w * h * 4];
            source.CopyPixels(buffer, w * 4, 0);
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                using var bitmap = new SKBitmap(
                    new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
                bitmap.SetPixels(pin.AddrOfPinnedObject());
                OcrResult result = await Task.Run(() => _engine!.Detect(bitmap, RapidOcrOptions.Default));
                return result.TextBlocks
                    .Select(b => new OcrLine(b.Text, BoundingRect(b.BoxPoints)))
                    .ToList();
            }
            finally
            {
                pin.Free();
            }
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>四角点 → 包围矩形（原图像素坐标）。</summary>
    private static Rect BoundingRect(SKPointI[] points)
    {
        if (points is not { Length: > 0 })
            return Rect.Empty;
        double minX = points.Min(p => p.X), minY = points.Min(p => p.Y);
        double maxX = points.Max(p => p.X), maxY = points.Max(p => p.Y);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>截图会话结束调用：释放引擎，下次识别重新加载模型。</summary>
    public static void Release()
    {
        if (_engine is null)
            return;
        try { _engine.Dispose(); }
        catch (Exception ex) { Log.Warning(ex, "OCR 引擎释放异常"); }
        _engine = null;
        Log.Debug("OCR 引擎已释放");
    }

    private static RapidOcr CreateEngine()
    {
        // 模型搜索序：插件资产目录优先，程序目录（旧布局）向后兼容
        string[] modelDirs =
        {
            Path.Combine(AppContext.BaseDirectory, "plugins", "screenshot", "assets", "models", "v5"),
            Path.Combine(AppContext.BaseDirectory, "models", "v5"),
        };
        string dir = modelDirs.FirstOrDefault(d =>
            File.Exists(Path.Combine(d, "ch_PP-OCRv5_mobile_det.onnx")))
        ?? throw new InvalidOperationException(
            $"未找到 OCR 模型：请将模型文件放到 plugins/screenshot/assets/models/v5/");
        Log.Debug("OCR 模型目录：{Dir}", dir);

        var ocr = new RapidOcr();
        ocr.InitModels(
            detPath: Path.Combine(dir, "ch_PP-OCRv5_mobile_det.onnx"),
            clsPath: Path.Combine(dir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            recPath: Path.Combine(dir, "ch_PP-OCRv5_rec_mobile.onnx"),
            keysPath: Path.Combine(dir, "ppocrv5_dict.txt"));
        return ocr;
    }
}
