using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 钉图窗口：把截图结果以置顶无边框小窗钉在屏幕上。
/// 位置与尺寸全部用物理像素维护（MoveWindowPhys 直摆，跨显示器 DPI
/// 变化时重断言，保持图像物理尺寸不变）。
/// 交互：拖动移动、滚轮缩放（光标处锚定）、焦点在钉图上按 ESC 关闭。
/// </summary>
internal partial class PinWindow : Window
{
    private const double MinZoom = 0.2;
    private const double MaxZoom = 5.0;
    private const double ZoomStep = 1.2;

    private readonly BitmapSource _image;
    private Point _originPhys;   // 窗口左上角（物理像素）
    private double _zoom = 1.0;  // 相对 1:1 物理像素的缩放

    private bool _dragging;
    private Point _dragCursorPhys;   // 按下时光标物理坐标
    private Point _dragOriginPhys;   // 按下时窗口原点

    internal PinWindow(BitmapSource image, Rect pinAtPhys, double initialZoom = 1.0)
    {
        InitializeComponent();
        _image = image;
        PinImage.Source = image;
        _originPhys = new Point(pinAtPhys.X, pinAtPhys.Y);
        _zoom = Math.Clamp(initialZoom, MinZoom, MaxZoom);

        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle(); // 先建 HWND 摆到位再显示，避免闪现
        ApplyPhysicalBounds();
        Loaded += (_, _) => ApplyPhysicalBounds();
        DpiChanged += (_, _) => ApplyPhysicalBounds(); // WPF 重缩放后重断言物理尺寸
        Show();
        ApplyPhysicalBounds();
        Log.Debug("钉图已显示：({X},{Y}) {Width}x{Height}@1x",
            (int)_originPhys.X, (int)_originPhys.Y, _image.PixelWidth, _image.PixelHeight);
    }

    private double PhysWidth => _image.PixelWidth * _zoom;
    private double PhysHeight => _image.PixelHeight * _zoom;

    private void ApplyPhysicalBounds()
    {
        GdiCapture.MoveWindowPhys(new WindowInteropHelper(this).Handle,
            new Rect(_originPhys.X, _originPhys.Y, PhysWidth, PhysHeight));
    }

    #region 交互

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragCursorPhys = PointToScreen(e.GetPosition(this)); // 物理，跨 DPI 拖动不失真
        _dragOriginPhys = _originPhys;
        CaptureMouse();
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        Point now = PointToScreen(e.GetPosition(this));
        _originPhys = new Point(
            _dragOriginPhys.X + now.X - _dragCursorPhys.X,
            _dragOriginPhys.Y + now.Y - _dragCursorPhys.Y);
        ApplyPhysicalBounds();
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        _dragging = false;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double newZoom = Math.Clamp(_zoom * (e.Delta > 0 ? ZoomStep : 1 / ZoomStep), MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.001)
            return;

        // 光标指向的图像位置在缩放前后保持不动
        Point cursor = PointToScreen(e.GetPosition(this));
        double fx = (cursor.X - _originPhys.X) / PhysWidth;
        double fy = (cursor.Y - _originPhys.Y) / PhysHeight;
        _zoom = newZoom;
        _originPhys = new Point(
            cursor.X - fx * PhysWidth,
            cursor.Y - fy * PhysHeight);
        ApplyPhysicalBounds();
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 焦点在钉图上（点一下即获得）时 ESC 关闭
        if (e.Key == Key.Escape)
        {
            Close();
            Log.Debug("钉图已关闭");
            e.Handled = true;
        }
    }

    #endregion
}
