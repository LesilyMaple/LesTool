using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using LesTool.Platform;
using Microsoft.Win32;
using Serilog;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 长截图结果预览：可滚动查看整张长图，保存 PNG / 钉图 / 丢弃。
/// </summary>
internal partial class LongPreviewWindow : Window
{
    private readonly BitmapSource _image;
    private readonly Rect _regionPhys;

    internal LongPreviewWindow(BitmapSource image, Rect regionPhys)
    {
        InitializeComponent();
        _image = image;
        _regionPhys = regionPhys;

        Preview.Source = image;
        InfoText.Text = $"{image.PixelWidth} × {image.PixelHeight}";

        // 1:1 物理像素显示：图片 DIP 尺寸 = 像素尺寸 / 屏幕缩放，
        // 每个图片像素正好对应一个物理像素，观感与原网页一致（否则 150% 缩放下会被拉伸发糊）
        double previewScale = GdiCapture.GetMonitorScale(regionPhys.TopLeft);
        Preview.Width = image.PixelWidth / previewScale;
        Preview.Height = image.PixelHeight / previewScale;

        // 初始尺寸：宽跟随捕获区（含留白）
        double scale = GdiCapture.GetMonitorScale(regionPhys.TopLeft);
        Rect work = GdiCapture.GetMonitorWorkArea(regionPhys.TopLeft);
        Width = Math.Clamp(regionPhys.Width / scale + 40, 420, 900);
        Height = Math.Min(760, Math.Max(420, work.IsEmpty ? 700 : work.Height / scale * 0.7));

        // 物理坐标定位（与录制预览同款：EnsureHandle + 直摆），全程同单位钳制，
        // 保证窗口框架连同标题栏完整落在工作区内
        double wPhys = Width * scale, hPhys = Height * scale;
        double x = regionPhys.X, y = regionPhys.Y;
        if (!work.IsEmpty)
        {
            x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - wPhys));
            y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - hPhys));
        }
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        GdiCapture.MoveWindowPhys(helper.Handle, new Rect(x, y, wPhys, hPhys));
        // 标题栏主题由 ThemeManager 的全局类处理器在 Loaded 时自动同步（迁移时移除的显式调用即此冗余）

        Show();
        Activate();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        Clipboard.SetImage(_image);
        Log.Debug("长截图已复制到剪贴板：{Width}x{Height}", _image.PixelWidth, _image.PixelHeight);
        ((Button)sender).Content = "已复制";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LesTool");
        Directory.CreateDirectory(dir);

        var dialog = new SaveFileDialog
        {
            Title = "保存长截图",
            Filter = "PNG 图片|*.png",
            FileName = $"longshot-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            InitialDirectory = dir,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_image));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
            Log.Information("长截图已保存：{Path}（{Width}x{Height}）",
                dialog.FileName, _image.PixelWidth, _image.PixelHeight);
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "长截图保存失败：{Path}", dialog.FileName);
            ToastService.Show($"保存失败：{ex.Message}，详见日志", 4000);
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        // 长图往往很高：钉图初始缩放以工作区高度的八成为限
        Rect work = GdiCapture.GetMonitorWorkArea(_regionPhys.TopLeft);
        double maxH = work.IsEmpty ? 1200 : work.Height * 0.8;
        double zoom = Math.Min(1.0, maxH / _image.PixelHeight);
        new PinWindow(_image, _regionPhys, zoom);
        Log.Debug("长截图已钉图：缩放 {Zoom:F2}", zoom);
        Close();
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
