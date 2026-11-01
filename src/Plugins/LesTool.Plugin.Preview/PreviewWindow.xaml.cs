using System.IO;
using System.Windows;
using System.Windows.Interop;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.Preview;

/// <summary>
/// 预览浮窗：QuickLook 式贴选中项右侧（空间不足放左侧），Hide 不销毁实现快开快关。
/// 窗口纪律：WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW 永不抢资源管理器焦点（键盘由全局钩子代收）；
/// 剥 WS_SYSMENU（NoSystemMenu 附加属性）；尺寸由 SizeToContent 按内容定，
/// 位置在布局完成后物理直摆（构造期 DPI 不可靠，铁律）；阴影由外层 Margin 留白承载。
/// </summary>
internal partial class PreviewWindow : Window
{
    private const double GapPhys = 12;      // 与锚点矩形的物理间距
    private const double MaxWorkAreaRatio = 0.7; // 窗口占工作区上限（防大图顶满屏）

    private double _scale = 1.0;            // 锚点所在显示器缩放（预览生命周期内不变）
    private Point _anchorCenterPhys;
    private Rect _lastAnchor;               // 最近一次定位锚点（异步填充换内容后重摆用）
    private Action? _pauseContent;          // 当前内容的停摆钩子（GIF 动画计时器/Office 解析取消/句柄 Unload）

    internal PreviewWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyNoActivate(new WindowInteropHelper(this).Handle);
        Closed += (_, _) => _pauseContent?.Invoke(); // 真关窗口：动画计时器一并停，防挂在 Dispatcher 上
    }

    /// <summary>呈现一组选中项（首开或跟随刷新）。内容替换后经布局再物理定位。</summary>
    public void Present(ExplorerSelection.Selection selection)
    {
        string path = selection.Paths[0];

        Rect anchor = selection.FirstItemBoundsPhys;
        if (anchor.IsEmpty)
            ExplorerSelection.TryGetForegroundWindowRect(out anchor);
        if (anchor.IsEmpty)
            anchor = new Rect(GdiCapture.GetCursorPosition(), new Size(1, 1));
        _lastAnchor = anchor;
        _anchorCenterPhys = new Point(anchor.X + anchor.Width / 2, anchor.Y + anchor.Height / 2);
        _scale = GdiCapture.GetMonitorScale(_anchorCenterPhys);

        var generation = ++_contentGeneration;
        var content = PreviewRenderers.Build(path, _scale, element => { if (generation == _contentGeneration) SwapContent(element); });
        _pauseContent?.Invoke(); // 跟随刷新换内容：旧内容（动画/解析任务/句柄）先停摆
        _pauseContent = content.Pause;
        // 内容尺寸上限先夹进工作区 70%（DIP）：SizeToContent 算出的窗口就不会超限闪现
        Rect work = GdiCapture.GetMonitorWorkArea(_anchorCenterPhys);
        if (!work.IsEmpty)
        {
            content.Element.MaxWidth = Math.Max(240, work.Width * MaxWorkAreaRatio / _scale - 40);
            content.Element.MaxHeight = Math.Max(240, work.Height * MaxWorkAreaRatio / _scale - 112);
        }
        Body.Content = content.Element;

        FileName.Text = Path.GetFileName(path);
        string meta = PreviewRenderers.TypeLabel(path);
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            meta += $" · {PreviewRenderers.FormatBytes(info.Length)} · {PreviewRenderers.FormatTime(info.LastWriteTime)}";
        }
        else if (Directory.Exists(path))
        {
            meta += $" · {PreviewRenderers.FormatTime(Directory.GetLastWriteTime(path))}";
        }
        if (selection.Paths.Count > 1)
            meta += $" · 已选 {selection.Paths.Count} 项，预览第 1 项";
        Meta.Text = meta;

        if (!IsVisible)
            Show();

        // SizeToContent 在布局后才定出最终尺寸，摆位必须排在其后（Loaded 优先级）
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            () => PositionWindow(anchor));
        Log.Debug("预览内容已填充：{Path}", path);
    }

    /// <summary>收起（Hide 保 HWND，快开不重建）；持续型内容（GIF 动画）一并停摆。</summary>
    public void Dismiss()
    {
        _contentGeneration++;
        _pauseContent?.Invoke();
        _pauseContent = null;
        Hide();
    }

    /// <summary>
    /// 异步填充换入真实内容（Office 解析完成回调）：换元素、待布局完成后按最近
    /// 锚点重摆窗口尺寸与位置。窗口已收起时丢弃（Pause 已取消后台任务，兜底双保险）。
    /// </summary>
    private long _contentGeneration;

    private void SwapContent(FrameworkElement element)
    {
        if (!IsVisible) return;
        Body.Content = element;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            () => PositionWindow(_lastAnchor));
    }

    /// <summary>物理定位：锚点右侧优先，放不下放左侧，最终夹进工作区。
    /// 窗口尺寸以 SizeToContent 的实际布局为准（DIP×缩放=物理），此处不再改动尺寸。</summary>
    private void PositionWindow(Rect anchor)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        Rect work = GdiCapture.GetMonitorWorkArea(_anchorCenterPhys);
        if (work.IsEmpty) return;

        double physW = Math.Min(ActualWidth * _scale, work.Width * MaxWorkAreaRatio);
        double physH = Math.Min(ActualHeight * _scale, work.Height * MaxWorkAreaRatio);

        double x = anchor.Right + GapPhys;
        if (x + physW > work.Right)
            x = anchor.Left - GapPhys - physW;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - physW));

        double y = anchor.Top + (anchor.Height - physH) / 2;
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - physH));

        GdiCapture.MoveWindowPhys(hwnd, new Rect(x, y, physW, physH));
        Log.Debug("预览窗已定位：({X:0},{Y:0}) {W:0}x{H:0} 物理", x, y, physW, physH);
    }

    /// <summary>永不成为活动窗口（轮盘窗口同款）：点内容滚动/选择不抢资源管理器焦点。</summary>
    private static void ApplyNoActivate(IntPtr hwnd)
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
