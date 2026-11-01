using System.Windows;
using System.Windows.Interop;
using LesTool.Platform;

namespace LesTool.Plugin.Screenshot;

/// <summary>结果类小窗的摆放（自 OcrResultWindow 抽出，QR 结果卡共用）。
/// 铁律：显示器坐标是物理像素，窗口 DIP 尺寸须先换算成物理再对齐，
/// 两套单位混算必偏移；且须在 Loaded 之后调用（构造期 DPI 不可靠）。</summary>
internal static class WindowCentering
{
    /// <summary>把窗口物理像素精确居中到选区所在显示器（选区不在任何屏则用第一块）。</summary>
    public static void CenterOnSelectionMonitor(Window window, Rect selectionPhys)
    {
        var monitors = GdiCapture.GetMonitorBounds();
        var center = new Point(selectionPhys.X + selectionPhys.Width / 2,
                               selectionPhys.Y + selectionPhys.Height / 2);
        Rect mon = monitors.FirstOrDefault(m => m.Contains(center));
        if (mon.IsEmpty)
            mon = monitors.FirstOrDefault();

        double scale = PresentationSource.FromVisual(window)?.CompositionTarget is { } ct
            ? ct.TransformToDevice.M11 : 1.0;
        double physW = window.ActualWidth * scale, physH = window.ActualHeight * scale;
        GdiCapture.MoveWindowPhys(new WindowInteropHelper(window).Handle, new Rect(
            mon.X + (mon.Width - physW) / 2,
            mon.Y + (mon.Height - physH) / 2,
            physW, physH));
    }
}
