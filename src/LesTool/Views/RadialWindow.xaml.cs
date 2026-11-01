using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using LesTool.Platform;
using LesTool.Services;
using Serilog;

namespace LesTool.Views;

/// <summary>
/// 悬浮轮盘窗口。无边框 + 全透明 + 不抢焦点 + 置顶、不在任务栏出现。
/// 扩展样式加 WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW：永不成为活动窗口，
/// Alt+F4 无法误关、不进 Alt+Tab（窗口被关后由控制器自愈重建兜底）。
/// 定位用物理像素直摆（钩子坐标是物理像素）；PerMonitorV2 下跨屏显示
/// 会触发 DPI 变化，窗口被 WPF 重缩放后重新对心。
/// 盘面（对齐 Quicker 的 24 槽）：内圈 0-7 + 外圈 8-15 可见，扩展圈 16-23 为
/// 盘外不可见区域（划出盘面按方向命中，仅全局场景生效，生效合成在控制器侧）。
/// 几何计算全部走 RadialGeometry（与设置页编辑器共用同一套数学）。窗口自身
/// 不接收任何鼠标输入——悬停/选择全由钩子通道的物理坐标驱动（UpdateHover），
/// 松开右键时控制器读 SelectedCommand 决定执行还是取消。中央小圆只显示
/// 当前所指动作的标题。
/// </summary>
public partial class RadialWindow : Window
{
    private const int VisibleSlots = RadialSettingsData.OuterFirst + RadialSettingsData.RingSlots; // 16

    // 扇区常态透明度：空槽/动作槽/中央小圆整盘一色，仅靠标签与悬停高亮区分（对齐 Quicker，2026-10-24 定案）
    private const double SectorIdleOpacity = 0.35;

    private Point _centerPx;       // 手势触发点（物理像素）
    private Point _visualCenterPx; // 实际绘制圆心：触发点被工作区夹紧后可能内移（边缘触发场景）
    private double _sizeDip = RadialGeometry.BaseSize;

    private readonly string?[] _actions = new string?[RadialSettingsData.SlotCount];
    private readonly Path[] _sectors = new Path[VisibleSlots];
    private readonly StackPanel[] _labels = new StackPanel[VisibleSlots];
    private int _hoverIndex = -1;

    public RadialWindow()
    {
        InitializeComponent();
        DpiChanged += (_, _) =>
        {
            if (IsVisible)
                PositionAtPhysicalCenter();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// 预创建 HWND 并完成一次布局渲染，把首次显示的开销移到启动期。
    /// 窗口内容全透明，短暂 Show 用户不可见。
    /// </summary>
    public void PrepareForFastShow()
    {
        Show();
        Hide();
    }

    /// <summary>应用轮盘直径（DIP）；显示前调用，随设置即时生效。尺寸变化时已画盘面就地重画。</summary>
    public void ApplySize(double sizeDip)
    {
        bool changed = Math.Abs(_sizeDip - sizeDip) > 0.01;
        _sizeDip = sizeDip;
        Width = sizeDip;
        Height = sizeDip;
        double unit = RadialGeometry.Unit(sizeDip);
        // 底盘只铺到扇区外缘（BaseOuter）：铺满整窗会在盘外多出一圈底色环（2026-10-21 实测"轮胎边"）
        BackdropEllipse.Width = RadialGeometry.BaseOuter * 2 * unit;
        BackdropEllipse.Height = BackdropEllipse.Width;
        CenterEllipse.Width = RadialGeometry.BaseCenter * 2 * unit;
        CenterEllipse.Height = CenterEllipse.Width;
        CenterTitle.Width = CenterEllipse.Width - 10;
        // 中央底色 = 扇区常态合成色（OverlayHoverBrush × SectorIdleOpacity）。克隆画刷而非改资源，
        // 每次显示重取：皮肤热重载后下次唤出即生效
        CenterEllipse.Fill = new SolidColorBrush(((SolidColorBrush)TryFindResource("OverlayHoverBrush")!).Color)
        {
            Opacity = SectorIdleOpacity,
        };
        if (changed && _sectors[0] != null)
            BuildSectors(); // 尺寸变了但未 Configure 的路径（保险；正常流程 Configure 会重画）
    }

    /// <summary>装载生效槽位命令（24 项，null=空槽）并重建盘面；显示前调用。</summary>
    public void Configure(IReadOnlyList<string?> actions)
    {
        for (var i = 0; i < _actions.Length; i++)
            _actions[i] = i < actions.Count ? actions[i] : null;
        BuildSectors();
    }

    /// <summary>在屏幕物理像素坐标 centerScreenPx 处居中显示；悬停态复位。</summary>
    public void ShowAt(Point centerScreenPx)
    {
        _centerPx = centerScreenPx;
        _hoverIndex = -1;
        ApplyHoverVisuals();
        UpdateCenter();
        PositionAtPhysicalCenter(recenterCursor: true);
        Show();
    }

    /// <summary>悬停命中的槽位下标（含扩展圈 16-23）；中央信息区返回 -1。</summary>
    public int HoverIndex => _hoverIndex;

    /// <summary>松开右键时应执行的命令；未悬停、悬停空槽或配置失效时为 null（=取消）。</summary>
    public string? SelectedCommand
    {
        get
        {
            var command = _hoverIndex >= 0 ? _actions[_hoverIndex] : null;
            return ActionRegistry.Find(command ?? "") != null ? command : null;
        }
    }

    /// <summary>按屏幕物理坐标更新悬停扇区（控制器在 UI 线程封送后调用）。</summary>
    public void UpdateHover(Point screenPx) => SetHover(HitTest(screenPx));

    /// <summary>命中：以实际绘制圆心（屏幕边缘触发时盘面会被夹进工作区，与触发点不同心）
    /// 判内外圈/扩展圈；几何委托 RadialGeometry（与编辑器同一套数学）。</summary>
    private int HitTest(Point screenPx)
    {
        double scale = GdiCapture.GetMonitorScale(_visualCenterPx);
        double dx = (screenPx.X - _visualCenterPx.X) / scale;
        double dy = (screenPx.Y - _visualCenterPx.Y) / scale;
        return RadialGeometry.SectorAt(dx, dy, _sizeDip);
    }

    private void SetHover(int index)
    {
        if (index == _hoverIndex) return;
        _hoverIndex = index;
        ApplyHoverVisuals();
        UpdateCenter();
        Log.Debug("轮盘悬停：槽 {Index}（{Name}）", index, index >= 0 ? DisplayNameOf(index) : "无");
    }

    /// <summary>按所在显示器 DPI 与工作区，把窗口中心对到 _centerPx（物理像素）。
    /// recenterCursor：显示时为 true——盘心被工作区夹进内移后，把指针一并挪到新盘心，
    /// 让边缘触发的方向感与屏幕中央一致（Quicker 同款）；DPI 变化重对心时不挪。</summary>
    private void PositionAtPhysicalCenter(bool recenterCursor = false)
    {
        if (!IsLoaded) return; // 预热阶段无布局尺寸

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        double scale = GdiCapture.GetMonitorScale(_centerPx);
        Rect work = GdiCapture.GetMonitorWorkArea(_centerPx);

        // 用显式 Width/Height 而非 ActualWidth：改尺寸的同一布局帧里 Actual 还是旧值
        double w = Width * scale;
        double h = Height * scale;
        double x, y;
        if (work.IsEmpty)
        {
            x = _centerPx.X - w / 2;
            y = _centerPx.Y - h / 2;
        }
        else
        {
            x = Math.Clamp(_centerPx.X - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
            y = Math.Clamp(_centerPx.Y - h / 2, work.Top, Math.Max(work.Top, work.Bottom - h));
        }
        // 命中判定用同一圆心：盘面被夹进工作区后，指着的扇区就是会被选中的扇区
        _visualCenterPx = new Point(x + w / 2, y + h / 2);
        GdiCapture.MoveWindowPhys(hwnd, new Rect(x, y, w, h));

        if (recenterCursor &&
            (Math.Abs(_visualCenterPx.X - _centerPx.X) > 0.5 || Math.Abs(_visualCenterPx.Y - _centerPx.Y) > 0.5))
        {
            if (SetCursorPos((int)Math.Round(_visualCenterPx.X), (int)Math.Round(_visualCenterPx.Y)))
                Log.Debug("边缘触发：指针已移至盘心（内移 {Dx:F0},{Dy:F0}）",
                    _visualCenterPx.X - _centerPx.X, _visualCenterPx.Y - _centerPx.Y);
        }
    }

    #region 盘面绘制

    private void BuildSectors()
    {
        RingCanvas.Children.Clear();
        for (var slot = 0; slot < VisibleSlots; slot++)
        {
            var sector = new Path
            {
                Data = RadialGeometry.SectorPath(_sizeDip, slot),
                Fill = (Brush)TryFindResource("OverlayHoverBrush")!,
                Stroke = (Brush)TryFindResource("OverlaySeparator")!,
                StrokeThickness = 1,
            };
            _sectors[slot] = sector;
            RingCanvas.Children.Add(sector);

            // 槽位标签：字形 + 命令名，摆在扇区角平分线上；空槽不摆
            var label = new StackPanel();
            if (_actions[slot] is { } id)
            {
                var command = ActionRegistry.Find(id);
                if (command != null)
                {
                    label.Children.Add(new TextBlock
                    {
                        Text = command.Glyph,
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = (double)TryFindResource("FontSizeBody")!,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = (Brush)TryFindResource("OverlayIconStroke")!,
                    });
                    label.Children.Add(new TextBlock
                    {
                        Text = command.Name,
                        FontSize = (double)TryFindResource("FontSizeCaption")!,
                        MaxWidth = RadialGeometry.LabelMaxWidth(_sizeDip, slot),
                        TextAlignment = TextAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = (Brush)TryFindResource("OverlayTextBrush")!,
                    });
                }
            }
            _labels[slot] = label;

            // Canvas 定位：先量出标签尺寸再按中心点对齐
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var center = RadialGeometry.LabelCenter(_sizeDip, slot);
            Canvas.SetLeft(label, center.X - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, center.Y - label.DesiredSize.Height / 2);
            RingCanvas.Children.Add(label);
        }
        ApplyHoverVisuals();
    }

    private void ApplyHoverVisuals()
    {
        if (_sectors[0] is null) return; // 尚未 Configure（启动期预热）
        var separator = (Brush)TryFindResource("OverlaySeparator")!;
        var accent = (Brush)TryFindResource("NavAccent")!;
        for (var slot = 0; slot < VisibleSlots; slot++)
        {
            // 选中 = 强调色软底 + 实心强调色描边：浅色强调直接叠深底会被去饱和成"脏红"
            // （玻璃拟态曾中招），描边保证任何皮肤下选中态一眼读出主题色
            var hovered = slot == _hoverIndex;
            _sectors[slot].Fill = hovered
                ? (Brush)TryFindResource("RadialSectorActiveBrush")!
                : (Brush)TryFindResource("OverlayHoverBrush")!;
            _sectors[slot].Stroke = hovered ? accent : separator;
            _sectors[slot].StrokeThickness = hovered ? 1.5 : 1;
            // 常态整盘同色；悬停恢复不透明，高亮在任何槽位（含空槽）都同样醒目
            _sectors[slot].Opacity = hovered ? 1 : SectorIdleOpacity;
        }
        // 扩展圈（16-23）不可见：不画扇区，命中反馈只在中央标题（UpdateCenter）
    }

    /// <summary>中央只显示当前所指动作的标题（用户拍板）；未指向任何槽位时留空。</summary>
    private void UpdateCenter()
    {
        CenterTitle.Text = _hoverIndex < 0
            ? ""
            : _actions[_hoverIndex] is { } id ? ActionRegistry.NameOf(id) : "空槽位";
    }

    private string DisplayNameOf(int index) =>
        _actions[index] is { } id ? ActionRegistry.NameOf(id) : "空槽";

    #endregion

    #region P/Invoke

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    #endregion
}
