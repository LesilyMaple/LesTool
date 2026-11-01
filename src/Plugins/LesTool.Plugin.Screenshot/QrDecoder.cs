using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using ZXing;
using ZXing.Common;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// ZXing.Net 二维码解码封装。纯托管、无原生库，reader 构造毫秒级——
/// 每次调用现建即用，无懒加载、无释放、无重入闸（与 OCR 的重引擎不同，
/// 重入闸在 ScreenshotService 按会话挡）。
/// 屏幕码大多一次命中；失败按梯度重试：原图 → 反色（深底白码）→
/// 2x 放大（码太小）→ 放大+反色。推理在线程池，不占 UI 线程。
/// </summary>
internal static class QrDecoder
{
    /// <summary>识别结果：文本 + 码在原图（物理像素）中的包围盒（定位点外接矩形，略内缩）。
    /// v1 结果卡不展示图，坐标先备着给后续对照图/多码扩展。</summary>
    public sealed record QrResult(string Text, Rect Box);

    /// <summary>识别图中二维码；未识别到返回 null。</summary>
    public static Task<QrResult?> RunAsync(BitmapSource image) => Task.Run(() => Decode(image));

    /// <summary>是否为可安全直开的 http/https 链接（其它 scheme 只显示文本，不给打开入口）。
    /// 主机名必须非空，滤掉 http:///x 这类畸形串。</summary>
    public static bool IsHttpUrl(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        uri.Host.Length > 0;

    private static QrResult? Decode(BitmapSource image)
    {
        // BitmapSource → BGRA → 灰度（与 OCR 同路：统一 Bgra32 整幅直拷）。
        // 后续重试都在灰度数组上做，零 WPF 依赖。
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = source.PixelWidth, h = source.PixelHeight;
        if (w < 1 || h < 1)
            return null;
        var bgra = new byte[w * h * 4];
        source.CopyPixels(bgra, w * 4, 0);
        byte[] gray = ToGray(bgra);

        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new[] { BarcodeFormat.QR_CODE },
            },
        };

        var sw = Stopwatch.StartNew();
        QrResult? hit = Try(reader, gray, w, h, 1.0, "原图")
                     ?? Try(reader, Invert(gray), w, h, 1.0, "反色");
        if (hit is null)
        {
            // 放大整份只在前面全失败后才做，大选区也只是一次额外分配
            byte[] gray2x = Upscale2x(gray, w, h);
            hit = Try(reader, gray2x, w * 2, h * 2, 2.0, "2x放大")
               ?? Try(reader, Invert(gray2x), w * 2, h * 2, 2.0, "2x放大+反色");
        }
        sw.Stop();
        if (hit is not null)
            Log.Debug("二维码解码耗时 {Ms}ms", sw.ElapsedMilliseconds);
        return hit;
    }

    /// <summary>单策略解码。scale = 该策略像素坐标 → 原图像素坐标的除数。</summary>
    private static QrResult? Try(BarcodeReaderGeneric reader, byte[] pixels, int w, int h,
        double scale, string strategy)
    {
        Result? result;
        try
        {
            result = reader.Decode(new GrayLuminanceSource(pixels, w, h));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "二维码解码异常（策略 {Strategy}）", strategy);
            return null;
        }
        if (result is null || string.IsNullOrEmpty(result.Text))
        {
            Log.Debug("二维码未命中（策略 {Strategy}）", strategy);
            return null;
        }

        Rect box = BoundingRect(result.ResultPoints);
        if (!box.IsEmpty)
            box = new Rect(box.X / scale, box.Y / scale, box.Width / scale, box.Height / scale);
        Log.Debug("二维码命中（策略 {Strategy}）：{Chars} 字符", strategy, result.Text.Length);
        return new QrResult(result.Text, box);
    }

    /// <summary>BGRA → 灰度（BT.601 亮度），ZXing 解码只吃灰度。</summary>
    private static byte[] ToGray(byte[] bgra)
    {
        var gray = new byte[bgra.Length / 4];
        for (int i = 0; i < gray.Length; i++)
        {
            int b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2];
            gray[i] = (byte)((r * 30 + g * 59 + b * 11) / 100);
        }
        return gray;
    }

    private static byte[] Invert(byte[] src)
    {
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i++)
            dst[i] = (byte)(255 - src[i]);
        return dst;
    }

    /// <summary>最近邻 2x 放大（每像素铺 2×2），保住小码的模块硬边缘。</summary>
    private static byte[] Upscale2x(byte[] src, int w, int h)
    {
        int w2 = w * 2;
        var dst = new byte[w2 * h * 2];
        for (int y = 0; y < h; y++)
        {
            int srcRow = y * w;
            int row0 = y * 2 * w2, row1 = row0 + w2;
            for (int x = 0; x < w; x++)
            {
                byte v = src[srcRow + x];
                int d = x * 2;
                dst[row0 + d] = v; dst[row0 + d + 1] = v;
                dst[row1 + d] = v; dst[row1 + d + 1] = v;
            }
        }
        return dst;
    }

    /// <summary>定位点 → 包围矩形（原图像素坐标）。</summary>
    private static Rect BoundingRect(ResultPoint[]? points)
    {
        if (points is not { Length: > 0 })
            return Rect.Empty;
        double minX = points.Min(p => p.X), minY = points.Min(p => p.Y);
        double maxX = points.Max(p => p.X), maxY = points.Max(p => p.Y);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>灰度数组直喂 ZXing；CreateLuminanceSource 供裁剪/旋转族复用。</summary>
    private sealed class GrayLuminanceSource : BaseLuminanceSource
    {
        public GrayLuminanceSource(byte[] luminances, int width, int height)
            : base(luminances, width, height)
        {
        }

        protected override LuminanceSource CreateLuminanceSource(byte[] newLuminances, int width, int height)
            => new GrayLuminanceSource(newLuminances, width, height);
    }
}
