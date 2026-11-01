using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Serilog;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 文字识别结果独立窗（QQ 屏幕识别同构）：左=识别原图 1:1 物理像素 + 半透明识别框，
/// 右=逐行文本（悬停互相高亮），右下复制全部。识别完成后由截图服务弹出并收摊遮罩，
/// 本窗独立存活：可缩放、可常驻、ESC 关闭。
/// 布局（1:1 尺寸、默认窗口大小、屏幕居中）全部推迟到 Loaded 后做——那时 DPI
/// 与呈现源才可靠；居中走物理像素（MoveWindowPhys），杜绝 DIP 与物理坐标混算偏移。
/// </summary>
internal partial class OcrResultWindow : Window
{
    private const double TextColumnDip = 480; // 与 XAML 第三列字面值同步

    private static readonly Brush BoxBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x32, 0xCD, 0x32));
    private static readonly Brush BoxHoverBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x32, 0xCD, 0x32));

    private readonly BitmapSource _image;
    private readonly IReadOnlyList<ScreenshotOcr.OcrLine> _lines;
    private readonly Rect _selectionPhys;
    private readonly List<Rectangle?> _boxes = new(); // 与文本行同序；无包围盒的行为 null

    internal OcrResultWindow(BitmapSource image, IReadOnlyList<ScreenshotOcr.OcrLine> lines, Rect selectionPhys)
    {
        InitializeComponent();
        FreezeBrushes();
        _image = image;
        _lines = lines;
        _selectionPhys = selectionPhys;
        OcrImage.Source = image;
        Loaded += OnFirstLoaded;
    }

    private static void FreezeBrushes()
    {
        BoxBrush.Freeze();
        BoxHoverBrush.Freeze();
    }

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        double scale = PresentationSource.FromVisual(this)?.CompositionTarget is { } ct
            ? ct.TransformToDevice.M11 : 1.0;

        // 1:1 物理像素：DIP 尺寸 = 像素 / 所在屏缩放
        double imgW = _image.PixelWidth / scale, imgH = _image.PixelHeight / scale;
        OcrImage.Width = imgW;
        OcrImage.Height = imgH;
        ImageHost.Width = imgW; // 画布与图同尺寸，识别框按同一比例定位
        ImageHost.Height = imgH;

        BuildLines(scale);
        OcrStat.Text = $"{_lines.Count} 行 · {_image.PixelWidth}×{_image.PixelHeight}";

        // 默认尺寸：图片 1:1 + 右栏，夹所在屏工作区（DIP，WPF 自管 DPI）
        double targetW = imgW + TextColumnDip + 1 + 16; // 右栏 + 分隔线 + 边框余量
        double targetH = imgH + 40 + 16;                // 标题栏 + 边框余量
        var wa = SystemParameters.WorkArea;
        Width = Math.Clamp(targetW, MinWidth, wa.Width * 0.95);
        Height = Math.Clamp(targetH, MinHeight, wa.Height * 0.92);

        CenterOnSelectionMonitor();
        Log.Information("文字识别窗已打开：{Lines} 行，图片 {W}×{H}",
            _lines.Count, _image.PixelWidth, _image.PixelHeight);
    }

    /// <summary>右栏逐行文本 + 图上识别框；行与框双向悬停联动。</summary>
    private void BuildLines(double scale)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            int index = i;
            Rect phys = _lines[i].Box;

            Rectangle? box = null;
            if (!phys.IsEmpty)
            {
                box = new Rectangle
                {
                    Fill = BoxBrush,
                    Width = Math.Max(2, phys.Width / scale),
                    Height = Math.Max(2, phys.Height / scale),
                };
                Canvas.SetLeft(box, phys.X / scale);
                Canvas.SetTop(box, phys.Y / scale);
                BoxLayer.Children.Add(box);
            }
            _boxes.Add(box);

            var line = new TextBox
            {
                Text = _lines[i].Text,
                IsReadOnly = true,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                MinHeight = 24,
                Padding = new Thickness(6, 3, 6, 3),
                TextWrapping = TextWrapping.NoWrap,
                Cursor = Cursors.IBeam,
            };
            line.SetResourceReference(ForegroundProperty, "TextPrimary");
            line.MouseEnter += (_, _) => SetHover(index, true);
            line.MouseLeave += (_, _) => SetHover(index, false);
            OcrLines.Children.Add(line);
        }
    }

    /// <summary>悬停联动：图中框加深，文本行铺导航悬停底色。</summary>
    private void SetHover(int index, bool on)
    {
        if (_boxes[index] is { } box)
            box.Fill = on ? BoxHoverBrush : BoxBrush;
        if (OcrLines.Children[index] is TextBox line)
            line.SetValue(BackgroundProperty,
                on ? TryFindResource("NavHoverBackground") : Brushes.Transparent);
    }

    /// <summary>物理像素精确居中到选区所在显示器（抽到 WindowCentering，与 QR 结果卡共用）。</summary>
    private void CenterOnSelectionMonitor() =>
        WindowCentering.CenterOnSelectionMonitor(this, _selectionPhys);

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        string all = string.Join(Environment.NewLine,
            OcrLines.Children.OfType<TextBox>().Select(t => t.Text));
        if (all.Length == 0)
            return;
        Clipboard.SetText(all);
        Log.Information("已复制全部识别文本（{Count} 行）", OcrLines.Children.Count);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }
}
