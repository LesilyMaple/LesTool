using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 每显示器一个的截图遮罩窗：整幅显示冻结图，选区/悬停窗外盖暗幕。
/// 几何决策在 SelectionSession（全局物理像素），标注状态在 AnnotationState（全局共享）。
/// 定位用 SetWindowPos 物理坐标直摆（PerMonitorV2 下 Left/Top 不可靠），
/// DIP↔物理换算比例取自本窗口的实时 TransformToDevice。
/// </summary>
internal partial class OverlayWindow : Window
{
    private const double HandleSize = 7;      // 手柄边长（DIP）
    private const double HandleHitRadius = 6; // 手柄命中半径（DIP）
    private const double HoverDetectIntervalMs = 12; // 悬停检测节流
    private const double MosaicRegenIntervalMs = 40; // 马赛克预览重生成节流
    private const double PenMinStepPx = 1.5;  // 画笔轨迹抽稀阈值（物理像素）

    private static readonly Brush HoverBorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
    private static readonly Brush SelBorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x9E, 0xFF));
    // QQ 式选中高亮：半透明圆角块随主题取（深=透明白，浅=透明黑），每次现取以便主题热切换
    private Brush ActiveBrush => (Brush)FindResource("OverlayActiveBrush");

    // 手柄序号与 SelectionSession.ApplyHandle 约定一致：0..7 = NW N NE E SE S SW W
    private static readonly Cursor[] HandleCursors =
    {
        Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
        Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
    };
    // 手柄位置查表：[行,列] 行=上中下 列=左中右，-1 表示中心无手柄
    private static readonly int[,] HandleMap = { { 0, 1, 2 }, { 7, -1, 3 }, { 6, 5, 4 } };

    private readonly SelectionSession _session;
    private readonly AnnotationState _annotations;
    private readonly Func<Rect, BitmapSource> _mosaicFactory;
    private readonly BitmapSource _frozenCrop;   // 本屏冻结图（放大镜读像素用）
    private readonly Rectangle[] _handles = new Rectangle[8];
    private readonly Dictionary<AnnotationTool, Button> _toolButtons = new();
    private readonly Button[] _colorButtons = new Button[6];

    private Rect? _hoverPhys;              // 悬停窗口矩形（全局物理像素），仅 None 状态维护
    private long _lastHoverDetectTicks;
    private long _lastMosaicTicks;
    private long _lastLoupeTicks;
    private string _lastColorHex = "";     // 放大镜当前像素色（Ctrl+C 复制用）
    private byte[]? _pixelBuf;             // 冻结图像素缓存（放大镜首次显示时建立）
    private int _cropW, _cropH, _cropStride;

    // 标注绘制中的临时状态（本窗口独占，鼠标捕获保证事件不漂移）
    private Annotation? _draft;
    private AnnotationTool _lastShapeKind = AnnotationTool.Rect; // 形状按钮恢复上次用的矩形/椭圆
    private Point _draftAnchorRel;         // 起点（相对选区左上角，物理像素）
    private List<Point>? _draftPoints;     // 画笔轨迹
    private BitmapSource? _draftMosaic;    // 马赛克预览图（节流生成）
    private TextBox? _textEditor;          // 文字输入框

    /// <summary>本显示器的物理像素边界（虚拟屏坐标系）。</summary>
    public Rect MonitorPhys { get; }

    /// <summary>本窗口 HWND（构造时已创建）。</summary>
    public IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    /// <summary>悬停检测要排除的窗口（各遮罩自己），显示前由 ScreenshotService 统一注入。</summary>
    internal IReadOnlySet<IntPtr>? ExcludeHandles { get; set; }

    // 供 AnnotationLayer 读取的只读视图
    internal SelectionSession Session => _session;
    internal AnnotationState Annotations => _annotations;
    internal Annotation? DraftAnnotation => _draft;
    internal double DpiScale => Scale;

    private static readonly IReadOnlySet<IntPtr> NoHandles = new HashSet<IntPtr>();

    /// <summary>本窗口实时 DIP→物理 倍率（PerMonitorV2 下随所在屏变化）。</summary>
    private double Scale { get; set; } = 1.0;

    internal OverlayWindow(BitmapSource frozenCrop, Rect monitorPhys, SelectionSession session,
        AnnotationState annotations, Func<Rect, BitmapSource> mosaicFactory)
    {
        InitializeComponent();
        _session = session;
        _annotations = annotations;
        _mosaicFactory = mosaicFactory;
        _frozenCrop = frozenCrop;
        MonitorPhys = monitorPhys;

        FrozenImage.Source = frozenCrop;
        AnnotationCanvas.Attach(this);

        // 物理定位：先建 HWND 摆到位（不显示），Show 时直接出现在正确位置
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        GdiCapture.MoveWindowPhys(helper.Handle, monitorPhys);

        for (int i = 0; i < _handles.Length; i++)
        {
            var handle = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
            };
            _handles[i] = handle;
            Paint.Children.Add(handle);
        }

        BuildToolbarButtons();
        // XAML 里不接 ValueChanged：解析期 Minimum/Maximum 赋值就会触发事件，那时字段还没就绪
        ThickSlider.ValueChanged += OnThickSliderChanged;
        _toolButtons[AnnotationTool.Rect] = BtnShapeRect;       // 形状合体按钮的子选择器，同样走高亮循环
        _toolButtons[AnnotationTool.Ellipse] = BtnShapeEllipse;
        _toolButtons[AnnotationTool.Arrow] = BtnToolArrow;
        _toolButtons[AnnotationTool.Pen] = BtnToolPen;
        _toolButtons[AnnotationTool.Text] = BtnToolText;
        _toolButtons[AnnotationTool.Badge] = BtnToolBadge;
        _toolButtons[AnnotationTool.Mosaic] = BtnToolMosaic;

        Loaded += (_, _) => { RefreshScale(); Render(); };
        DpiChanged += (_, _) =>
        {
            RefreshScale();
            // WPF 在 DPI 变化时会按自己的理解重设窗口尺寸，重新断言物理边界
            GdiCapture.MoveWindowPhys(new WindowInteropHelper(this).Handle, MonitorPhys);
            Render();
        };
        _session.Changed += OnSessionChanged;
        _annotations.Changed += OnAnnotationsChanged;

        UpdateToolbarUi();
        Render();
    }

    /// <summary>显示窗口并确保物理边界精确盖住本显示器。</summary>
    public void ShowAtPhysicalBounds()
    {
        Show();
        GdiCapture.MoveWindowPhys(new WindowInteropHelper(this).Handle, MonitorPhys);
        RefreshScale();
    }

    private void RefreshScale()
    {
        Matrix? m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        if (m is { } matrix && matrix.M11 > 0)
            Scale = matrix.M11;
    }

    #region 坐标换算（物理像素 ↔ 本窗口 DIP；选区相对坐标）

    private Point ToGlobal(Point local) =>
        new(local.X * Scale + MonitorPhys.X, local.Y * Scale + MonitorPhys.Y);

    private Rect ToLocal(Rect phys) => new(
        (phys.X - MonitorPhys.X) / Scale, (phys.Y - MonitorPhys.Y) / Scale,
        phys.Width / Scale, phys.Height / Scale);

    /// <summary>全局物理坐标 → 相对选区左上角（标注坐标系）。</summary>
    private Point ToSelRel(Point global) => new(
        global.X - _session.Selection.X, global.Y - _session.Selection.Y);

    /// <summary>选区相对矩形 → 全局物理矩形。</summary>
    private Rect ToSelPhys(Rect rel) => new(
        _session.Selection.X + rel.X, _session.Selection.Y + rel.Y, rel.Width, rel.Height);

    private bool InsideSelection(Point global) =>
        !_session.Selection.IsEmpty && _session.Selection.Contains(global);

    #endregion

    #region 输入路由

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Point local = e.GetPosition(Root);

        if (e.OriginalSource is Visual source)
        {
            // 工具栏按钮自己的点击：若有文字在输入，先定稿再让按钮生效
            if (Toolbar.IsAncestorOf(source))
            {
                if (_textEditor != null) CommitText();
                return;
            }

            // 文字输入中：点别处=定稿当前文字；点在输入框内=继续编辑（默认行为）
            if (_textEditor != null)
            {
                bool inEditor = _textEditor.IsAncestorOf(source);
                CommitText();
                if (inEditor) return;
                if (_annotations.EditMode && _annotations.Tool == AnnotationTool.Text &&
                    InsideSelection(ToGlobal(local)))
                {
                    OpenTextEditor(local); // 文字工具下连续落点连续输入
                }
                return;
            }
        }

        if (_annotations.EditMode)
        {
            Activate();
            Point global = ToGlobal(local);
            if (_annotations.Tool is { } tool && InsideSelection(global))
            {
                if (tool == AnnotationTool.Text)
                    OpenTextEditor(local);
                else if (tool == AnnotationTool.Badge)
                {
                    // 序号：点击即落章，无需拖拽
                    _annotations.Add(new Annotation
                    {
                        Tool = AnnotationTool.Badge,
                        Color = AnnotationState.Palette[_annotations.ColorIndex],
                        Thickness = _annotations.Thickness,
                        PosRel = ToSelRel(global),
                        Number = _annotations.NextBadgeNumber(),
                    });
                }
                else
                {
                    StartDraft(tool, global);
                    Root.CaptureMouse();
                }
            }
            // 编辑模式下点选区外：忽略
            return;
        }

        Activate(); // 夺回键盘焦点：Alt+Tab 后回来点一下，ESC/回车仍可用
        var (zone, handle) = HitTest(local);
        _session.MouseDown(ToGlobal(local), zone, handle);
        Root.CaptureMouse(); // 拖拽可能跨到其他遮罩，捕获保证 move/up 不丢
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        Point local = e.GetPosition(Root);
        UpdateMagnifier(local); // 放大镜只在框选阶段显示，内部自判状态

        if (_draft != null)
        {
            UpdateDraft(ToGlobal(local));
            return;
        }

        if (_annotations.EditMode)
        {
            bool inside = InsideSelection(ToGlobal(local));
            Root.Cursor = _annotations.Tool is null || !inside
                ? Cursors.Arrow
                : _annotations.Tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross;
            return;
        }

        if (_session.Current is SelectionSession.State.DraggingNew
            or SelectionSession.State.DraggingMove
            or SelectionSession.State.DraggingHandle)
        {
            _session.MouseMove(ToGlobal(local));
        }
        else
        {
            var (zone, handle) = HitTest(local);
            UpdateCursor(zone, handle);
            if (_session.Current == SelectionSession.State.None)
                UpdateHover(ToGlobal(local));
        }
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Root.IsMouseCaptured)
            Root.ReleaseMouseCapture();
        if (_draft != null)
        {
            FinishDraft();
            return;
        }
        if (_annotations.EditMode || _textEditor != null)
            return;
        // 拖拽过小（误触级别）时，单击采纳悬停窗口
        _session.MouseUp(ToGlobal(e.GetPosition(Root)), _hoverPhys);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 逐层退出：输入/绘制 → 色板 → 工具（取消即退出编辑） → 整个截图
            if (_textEditor != null) CancelText();
            else if (_draft != null) { _draft = null; _draftPoints = null; RepaintAnnotations(); }
            else if (SubColors.Visibility == Visibility.Visible)
            {
                SubColors.Visibility = Visibility.Collapsed;
                BtnColorDroplet.Background = null;
            }
            else if (_annotations.Tool != null) _annotations.SetTool(null);
            else _session.Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_textEditor != null) CommitText();
            else
            {
                _draft = null; // 画了一半的图形丢弃
                _session.Confirm();
            }
            e.Handled = true;
        }
        else if (HandleAnnotationShortcuts(e))
        {
            e.Handled = true;
        }
    }

    /// <summary>编辑模式的 Ctrl+Z/Y 撤销重做、框选阶段的方向键微调和 Ctrl+C 取色。</summary>
    private bool HandleAnnotationShortcuts(KeyEventArgs e)
    {
        if (_textEditor != null)
            return false; // 文字输入时键盘归输入框

        var modifiers = Keyboard.Modifiers;

        // Ctrl+Z 撤销 / Ctrl+Shift+Z、Ctrl+Y 重做（编辑模式）
        if (_annotations.EditMode && modifiers.HasFlag(ModifierKeys.Control))
        {
            bool shift = modifiers.HasFlag(ModifierKeys.Shift);
            if (e.Key == Key.Z && !shift)
            {
                if (_draft != null) { _draft = null; _draftPoints = null; RepaintAnnotations(); }
                _annotations.Undo();
                return true;
            }
            if ((e.Key == Key.Z && shift) || e.Key == Key.Y)
            {
                if (_draft != null) { _draft = null; _draftPoints = null; RepaintAnnotations(); }
                _annotations.Redo();
                return true;
            }
        }

        // Ctrl+C 复制放大镜当前色值（放大镜可见时）
        if (e.Key == Key.C && modifiers == ModifierKeys.Control &&
            Loupe.Visibility == Visibility.Visible && _lastColorHex.Length > 0)
        {
            Clipboard.SetText(_lastColorHex);
            Log.Debug("已复制色值 {Hex}", _lastColorHex);
            return true;
        }

        // 方向键逐像素微调选区：普通=平移，Shift=扩边（仅已选定、非编辑模式）
        if (_session.Current == SelectionSession.State.Selected && !_annotations.EditMode)
        {
            (double dx, double dy) = e.Key switch
            {
                Key.Left => (-1, 0),
                Key.Right => (1, 0),
                Key.Up => (0, -1),
                Key.Down => (0, 1),
                _ => (0, 0),
            };
            if (dx != 0 || dy != 0)
            {
                _session.Nudge(dx, dy, moveAll: modifiers != ModifierKeys.Shift);
                return true;
            }
        }
        return false;
    }

    #endregion

    #region 命中测试与光标

    /// <summary>选区按下前的命中测试（本窗口 DIP 坐标系）。</summary>
    private (SelectionSession.Zone Zone, int Handle) HitTest(Point local)
    {
        if (_session.Current == SelectionSession.State.Selected)
        {
            Rect sel = ToLocal(_session.Selection);
            double[] xs = { sel.Left, (sel.Left + sel.Right) / 2, sel.Right };
            double[] ys = { sel.Top, (sel.Top + sel.Bottom) / 2, sel.Bottom };
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    int index = HandleMap[row, col];
                    if (index < 0) continue;
                    if (Math.Abs(local.X - xs[col]) <= HandleHitRadius &&
                        Math.Abs(local.Y - ys[row]) <= HandleHitRadius)
                        return (SelectionSession.Zone.Handle, index);
                }
            }
            if (sel.Contains(local))
                return (SelectionSession.Zone.Inside, -1);
        }
        return (SelectionSession.Zone.Outside, -1);
    }

    private void UpdateCursor(SelectionSession.Zone zone, int handle)
    {
        Root.Cursor = zone switch
        {
            SelectionSession.Zone.Handle => HandleCursors[handle],
            SelectionSession.Zone.Inside => Cursors.SizeAll,
            _ => Cursors.Cross,
        };
    }

    #endregion

    #region 悬停窗口检测

    private void UpdateHover(Point globalPhys)
    {
        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastHoverDetectTicks).TotalMilliseconds < HoverDetectIntervalMs)
            return;

        _lastHoverDetectTicks = now;
        // 检测落空（桌面壁纸层不算窗口）时兜底框住整个显示器
        Rect? rect = WindowDetector.FindAtPoint(globalPhys, ExcludeHandles ?? NoHandles) ?? MonitorPhys;
        bool changed = rect is { } newer
            ? _hoverPhys is not { } older || !AreClose(older, newer)
            : _hoverPhys is not null;
        if (changed)
        {
            _hoverPhys = rect;
            Render();
        }
    }

    private static bool AreClose(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) < 1 && Math.Abs(a.Y - b.Y) < 1 &&
        Math.Abs(a.Width - b.Width) < 1 && Math.Abs(a.Height - b.Height) < 1;

    #endregion

    #region 标注绘制

    private void StartDraft(AnnotationTool tool, Point global)
    {
        _draftAnchorRel = ToSelRel(global);
        _draftPoints = tool == AnnotationTool.Pen ? new List<Point> { _draftAnchorRel } : null;
        _draftMosaic = null;
        _draft = BuildDraft(tool, _draftAnchorRel, _draftAnchorRel, null);
        RepaintAnnotations();
    }

    private void UpdateDraft(Point global)
    {
        if (_draft is not { Tool: { } tool })
            return;
        Point rel = ToSelRel(global);

        if (tool == AnnotationTool.Pen)
        {
            if (_draftPoints is not { Count: > 0 }) return;
            Point last = _draftPoints[^1];
            if (Math.Abs(rel.X - last.X) + Math.Abs(rel.Y - last.Y) < PenMinStepPx) return;
            _draftPoints.Add(rel);
            _draft = BuildDraft(tool, _draftAnchorRel, rel, null) with { Points = _draftPoints };
        }
        else if (tool == AnnotationTool.Mosaic)
        {
            _draft = BuildDraft(tool, _draftAnchorRel, rel, _draftMosaic);
            // 马赛克预览节流重生成，提交时再精确生成一次
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastMosaicTicks).TotalMilliseconds >= MosaicRegenIntervalMs)
            {
                _lastMosaicTicks = now;
                _draftMosaic = _mosaicFactory(ToSelPhys(_draft.BoundsRel));
                _draft = _draft with { Mosaic = _draftMosaic };
            }
        }
        else
        {
            _draft = BuildDraft(tool, _draftAnchorRel, rel, null);
        }
        RepaintAnnotations();
    }

    private Annotation BuildDraft(AnnotationTool tool, Point start, Point end, BitmapSource? mosaic)
    {
        Rect bounds = new(
            Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        return new Annotation
        {
            Tool = tool,
            Color = AnnotationState.Palette[_annotations.ColorIndex],
            Thickness = _annotations.Thickness,
            BoundsRel = bounds,
            StartRel = start,
            EndRel = end,
            Mosaic = mosaic,
        };
    }

    private void FinishDraft()
    {
        if (_draft is not { } draft)
            return;
        _draft = null;
        _draftPoints = null;

        bool keep = draft.Tool switch
        {
            AnnotationTool.Rect or AnnotationTool.Ellipse or AnnotationTool.Mosaic
                => draft.BoundsRel.Width >= 2 && draft.BoundsRel.Height >= 2,
            AnnotationTool.Arrow => (draft.EndRel - draft.StartRel).Length >= 4,
            AnnotationTool.Pen => draft.Points is { Count: >= 2 },
            _ => false,
        };
        if (keep)
        {
            if (draft.Tool == AnnotationTool.Mosaic)
                draft = draft with { Mosaic = _mosaicFactory(ToSelPhys(draft.BoundsRel)) };
            _annotations.Add(draft);
            Log.Debug("标注已提交：{Tool}", draft.Tool);
        }
        RepaintAnnotations();
    }

    private void RepaintAnnotations() => AnnotationCanvas.InvalidateVisual();

    #endregion

    #region 文字输入

    private void OpenTextEditor(Point localDip)
    {
        CancelText();
        Color color = AnnotationState.Palette[_annotations.ColorIndex];
        double fontPx = AnnotationState.FontPx(_annotations.Thickness);
        var editor = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(color),
            CaretBrush = new SolidColorBrush(color),
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = fontPx / Scale,
            MinWidth = 12,
            Padding = new Thickness(0),
        };
        Canvas.SetLeft(editor, localDip.X);
        Canvas.SetTop(editor, localDip.Y);
        Paint.Children.Add(editor);
        _textEditor = editor;
        editor.Focus();
    }

    private void CommitText()
    {
        if (_textEditor is not { } editor)
            return;
        _textEditor = null;

        string text = editor.Text;
        Point posRel = ToSelRel(ToGlobal(new Point(Canvas.GetLeft(editor), Canvas.GetTop(editor))));
        double fontPx = editor.FontSize * Scale;
        Color color = ((SolidColorBrush)editor.Foreground).Color;

        Paint.Children.Remove(editor);
        Focus(); // 焦点还给窗口，键盘分层继续工作

        if (!string.IsNullOrWhiteSpace(text))
        {
            _annotations.Add(new Annotation
            {
                Tool = AnnotationTool.Text,
                Color = color,
                Thickness = _annotations.Thickness,
                Text = text,
                PosRel = posRel,
                FontSizePx = fontPx,
            });
            Log.Debug("文字标注已提交：{Length} 字", text.Length);
        }
    }

    private void CancelText()
    {
        if (_textEditor is not { } editor)
            return;
        _textEditor = null;
        Paint.Children.Remove(editor);
        Focus();
    }

    #endregion

    #region 工具栏

    private void BuildToolbarButtons()
    {
        // 第三排色板：6 个色块，选中态由 UpdateToolbarUi 置半透明白高亮
        for (int i = 0; i < AnnotationState.Palette.Length; i++)
        {
            Color color = AnnotationState.Palette[i];
            var button = new Button
            {
                Style = (Style)FindResource("TbBtn"),
                Width = 32,
                Tag = i,
                ToolTip = "颜色",
                Content = new Rectangle
                {
                    Fill = new SolidColorBrush(color),
                    Width = 16, Height = 16, RadiusX = 3, RadiusY = 3,
                },
            };
            button.Click += OnColorClick;
            _colorButtons[i] = button;
            ColorSwatches.Children.Add(button);
        }
    }

    /// <summary>形状/箭头/画笔/文字/序号带第二排（形状=矩形/椭圆），其余工具没有。</summary>
    private static bool ToolHasSubRow(AnnotationTool? tool) =>
        tool is AnnotationTool.Rect or AnnotationTool.Ellipse
            or AnnotationTool.Arrow or AnnotationTool.Pen
            or AnnotationTool.Text or AnnotationTool.Badge;

    /// <summary>工具、形状种类、颜色、粗细、第二/三排展开、撤销可用性的同步。</summary>
    private void UpdateToolbarUi()
    {
        BtnUndo.IsEnabled = _annotations.HasItems;
        BtnRedo.IsEnabled = _annotations.HasRedo;

        foreach ((AnnotationTool tool, Button button) in _toolButtons)
            button.Background = _annotations.Tool == tool ? ActiveBrush : null;

        // 形状合体按钮：矩形/椭圆任一选中即高亮；第二排左侧选择器只在形状工具时出现
        bool shapeTool = _annotations.Tool is AnnotationTool.Rect or AnnotationTool.Ellipse;
        BtnToolShape.Background = shapeTool ? ActiveBrush : null;
        ShapeKindPanel.Visibility = shapeTool ? Visibility.Visible : Visibility.Collapsed;

        // 第二排只跟这些工具走；工具收起时色板跟着收
        bool sub = ToolHasSubRow(_annotations.Tool);
        SubThickness.Visibility = sub ? Visibility.Visible : Visibility.Collapsed;
        if (!sub) SubColors.Visibility = Visibility.Collapsed;

        // 滑条与数值（等值赋值不触发 ValueChanged，不会回环）
        if (Math.Abs(ThickSlider.Value - _annotations.Thickness) >= 0.01)
            ThickSlider.Value = _annotations.Thickness;
        ThickValue.Text = ((int)Math.Round(_annotations.Thickness)).ToString();
        ColorDropletSwatch.Fill = new SolidColorBrush(AnnotationState.Palette[_annotations.ColorIndex]);
        BtnColorDroplet.Background = SubColors.Visibility == Visibility.Visible ? ActiveBrush : null;

        for (int i = 0; i < _colorButtons.Length; i++)
            _colorButtons[i].Background = _annotations.ColorIndex == i ? ActiveBrush : null;
    }

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;
        if (tag == "Shape")
        {
            // 形状合体按钮：选中态再点=取消选择，否则恢复上次用的形状（默认矩形）
            bool active = _annotations.Tool is AnnotationTool.Rect or AnnotationTool.Ellipse;
            _annotations.SetTool(active ? null : _lastShapeKind);
            return;
        }
        if (!Enum.TryParse<AnnotationTool>(tag, out var tool))
            return;
        if (tool is AnnotationTool.Rect or AnnotationTool.Ellipse)
            _lastShapeKind = tool;
        _annotations.SetTool(_annotations.Tool == tool ? null : tool); // 再点一次取消选择
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_textEditor != null) CommitText();
        if (_draft != null) { _draft = null; _draftPoints = null; RepaintAnnotations(); }
        _annotations.Undo();
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        if (_textEditor != null) CommitText();
        if (_draft != null) { _draft = null; _draftPoints = null; RepaintAnnotations(); }
        _annotations.Redo();
    }

    private void OnColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index })
            _annotations.SetColor(index); // 色板保持展开，方便连试几个色
    }

    private void OnThickSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _annotations.SetThickness(e.NewValue); // State 等值短路，不会回环
    }

    private void OnColorDropletClick(object sender, RoutedEventArgs e)
    {
        bool open = SubColors.Visibility != Visibility.Visible;
        SubColors.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        BtnColorDroplet.Background = open ? ActiveBrush : null;
        Log.Debug("色板展开：{Open}", open);
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (_textEditor != null) CommitText();
        _draft = null; // 未完成的图形丢弃
        _session.Confirm();
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (_textEditor != null) CommitText();
        _draft = null;
        _session.RequestPin();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_textEditor != null) CommitText();
        _draft = null;
        _session.RequestSave();
    }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (_textEditor != null) CommitText();
        _draft = null;
        _session.RequestRecord();
    }

    private void OnOcrClick(object sender, RoutedEventArgs e)
    {
        if (_textEditor != null) CommitText();
        _draft = null;
        _session.RequestOcr();
    }

    private void OnQrClick(object sender, RoutedEventArgs e)
    {
        if (_textEditor != null) CommitText();
        _draft = null;
        _session.RequestQr();
    }

    private void OnLongClick(object sender, RoutedEventArgs e)
    {
        if (_textEditor != null) CommitText();
        _draft = null;
        _session.RequestLongShot();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _session.Cancel();

    #endregion

    #region 放大镜 + 取色器

    /// <summary>框选阶段（无选区/拖新选区）显示放大镜；其余状态隐藏。</summary>
    private void UpdateMagnifier(Point local)
    {
        bool show = _session.Current is SelectionSession.State.None
            or SelectionSession.State.DraggingNew;
        if (!show)
        {
            if (Loupe.Visibility != Visibility.Collapsed)
                Loupe.Visibility = Visibility.Collapsed;
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastLoupeTicks).TotalMilliseconds < 8)
            return;
        _lastLoupeTicks = now;

        EnsurePixelBuffer();
        int px = Math.Clamp((int)(local.X * Scale), 0, _cropW - 1);
        int py = Math.Clamp((int)(local.Y * Scale), 0, _cropH - 1);

        // 当前像素颜色
        int i = py * _cropStride + px * 4;
        byte b = _pixelBuf![i], g = _pixelBuf[i + 1], r = _pixelBuf[i + 2];
        _lastColorHex = $"#{r:X2}{g:X2}{b:X2}";
        LoupeSwatch.Fill = new SolidColorBrush(Color.FromRgb(r, g, b));
        LoupeColor.Text = $"{_lastColorHex}  {r},{g},{b}";

        // 9×9 邻域放大 9 倍显示
        int sx = Math.Clamp(px - 4, 0, Math.Max(0, _cropW - 9));
        int sy = Math.Clamp(py - 4, 0, Math.Max(0, _cropH - 9));
        var crop = new CroppedBitmap(_frozenCrop, new Int32Rect(sx, sy, 9, 9));
        crop.Freeze();
        LoupePixels.Source = crop;

        // 跟随光标摆放，出屏向另一侧翻
        double cw = ActualWidth > 0 ? ActualWidth : MonitorPhys.Width / Scale;
        double ch = ActualHeight > 0 ? ActualHeight : MonitorPhys.Height / Scale;
        Loupe.Visibility = Visibility.Visible;
        AutoMeasure(Loupe);
        double lw = Loupe.DesiredSize.Width;
        double lh = Loupe.DesiredSize.Height;
        double x = local.X + 18;
        double y = local.Y + 18;
        if (x + lw > cw) x = Math.Max(0, local.X - lw - 18);
        if (y + lh > ch) y = Math.Max(0, local.Y - lh - 18);
        Position(Loupe, x, y);
    }

    /// <summary>把冻结图整幅拷进内存（约 33MB/4K，首次显示放大镜时做一次）。</summary>
    private void EnsurePixelBuffer()
    {
        if (_pixelBuf is not null)
            return;
        _cropW = _frozenCrop.PixelWidth;
        _cropH = _frozenCrop.PixelHeight;
        _cropStride = _cropW * 4;
        _pixelBuf = new byte[_cropStride * _cropH];
        _frozenCrop.CopyPixels(_pixelBuf, _cropStride, 0);
    }

    #endregion

    #region 渲染

    private void OnSessionChanged()
    {
        // 只有进入"已选定"才清悬停矩形；拖拽过程必须保留——
        // 松手时的"单击采纳窗口"要靠它拿到按下前的窗口矩形
        if (_session.Current == SelectionSession.State.Selected)
            _hoverPhys = null;
        Render();
    }

    private void OnAnnotationsChanged()
    {
        UpdateToolbarUi();
        RepaintAnnotations();
        Render(); // 第二行显隐改变工具栏尺寸，需要重新定位
    }

    private void Render()
    {
        double cw = ActualWidth > 0 ? ActualWidth : MonitorPhys.Width / Scale;
        double ch = ActualHeight > 0 ? ActualHeight : MonitorPhys.Height / Scale;

        // None 状态显示悬停窗口高亮；其余状态显示选区
        bool hovering = _session.Current == SelectionSession.State.None && _hoverPhys is { } hover;
        Rect phys = hovering ? _hoverPhys!.Value : _session.Selection;

        // WPF 的 Rect.Empty 宽高是 -∞，绝不能进 ToLocal；
        // 空选区、尚未拖出尺寸的选区都按"无高亮"整屏暗幕渲染
        bool invalid = phys.IsEmpty || phys.Width <= 0 || phys.Height <= 0 ||
                       double.IsNaN(phys.Width) || double.IsNaN(phys.Height) ||
                       double.IsInfinity(phys.Width) || double.IsInfinity(phys.Height);
        if (invalid)
        {
            // Empty 是正常空选区；其余数值属于异常，记录便于排查
            if (!phys.IsEmpty)
                Log.Warning("渲染拦截非法矩形：state={State} sel=({SelW}x{SelH}) hover=({HoverW}x{HoverH})",
                    _session.Current, _session.Selection.Width, _session.Selection.Height,
                    _hoverPhys?.Width, _hoverPhys?.Height);
            RenderEmpty(cw, ch);
            return;
        }

        Rect sel = ToLocal(phys);

        // 高亮区外围四块暗幕
        SetRect(DimTop, 0, 0, cw, Math.Max(0, sel.Top));
        SetRect(DimLeft, 0, sel.Top, Math.Max(0, sel.Left), Math.Max(0, sel.Height));
        SetRect(DimRight, sel.Right, sel.Top, Math.Max(0, cw - sel.Right), Math.Max(0, sel.Height));
        SetRect(DimBottom, 0, sel.Bottom, cw, Math.Max(0, ch - sel.Bottom));

        SelBorder.Stroke = hovering ? HoverBorderBrush : SelBorderBrush;
        SelBorder.Visibility = Visibility.Visible;
        SetRect(SelBorder, sel.X, sel.Y, sel.Width, sel.Height);

        // 手柄：仅已选定/微调时显示；悬停高亮、新建拖拽和编辑模式（选区锁定）不显示
        bool showHandles = !hovering && !_annotations.EditMode &&
            _session.Current is SelectionSession.State.Selected
            or SelectionSession.State.DraggingMove
            or SelectionSession.State.DraggingHandle;
        double[] xs = { sel.Left, (sel.Left + sel.Right) / 2, sel.Right };
        double[] ys = { sel.Top, (sel.Top + sel.Bottom) / 2, sel.Bottom };
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                int index = HandleMap[row, col];
                if (index < 0) continue;
                Rectangle handle = _handles[index];
                handle.Visibility = showHandles ? Visibility.Visible : Visibility.Collapsed;
                if (showHandles)
                    SetRect(handle, xs[col] - HandleSize / 2, ys[row] - HandleSize / 2,
                        HandleSize, HandleSize);
            }
        }

        // 尺寸标签：高亮区左上方，贴边时翻到下方/右移
        // （内容自适应尺寸：先把旧的显式宽高清掉再量，否则显式尺寸会反向钳制测量结果）
        SizeText.Text = $"{(int)Math.Round(phys.Width)} × {(int)Math.Round(phys.Height)}";
        SizeTip.Visibility = Visibility.Visible;
        AutoMeasure(SizeTip);
        double tipW = SizeTip.DesiredSize.Width;
        double tipH = SizeTip.DesiredSize.Height;
        double tipX = Math.Clamp(sel.X, 0, Math.Max(0, cw - tipW));
        double tipY = sel.Y - tipH - 4;
        if (tipY < 0) tipY = sel.Bottom + 4;
        Position(SizeTip, tipX, tipY);

        // 工具栏：仅选区定稿后出现；只在选区右下角所在的遮罩上画一份
        if (!hovering && _session.Current == SelectionSession.State.Selected)
        {
            var anchorPhys = new Point(_session.Selection.Right - 2, _session.Selection.Bottom - 2);
            if (MonitorPhys.Contains(anchorPhys))
            {
                Toolbar.Visibility = Visibility.Visible;
                AutoMeasure(Toolbar);
                double tbW = Toolbar.DesiredSize.Width;
                double tbH = Toolbar.DesiredSize.Height;
                // 右对齐选区右缘、贴选区下沿 +8，放不下翻到选区上方
                double tbX = Math.Clamp(sel.Right - tbW, 4, Math.Max(4, cw - tbW - 4));
                double tbY = sel.Bottom + 8;
                if (tbY + tbH > ch)
                    tbY = Math.Max(4, sel.Top - tbH - 8);
                Position(Toolbar, tbX, tbY);
            }
            else
            {
                Toolbar.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            Toolbar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>无高亮状态：整屏暗幕，隐藏选区/悬停的一切可视化元素。</summary>
    private void RenderEmpty(double cw, double ch)
    {
        SetRect(DimTop, 0, 0, cw, ch);
        SetRect(DimLeft, 0, 0, 0, 0);
        SetRect(DimRight, 0, 0, 0, 0);
        SetRect(DimBottom, 0, 0, 0, 0);
        SelBorder.Visibility = Visibility.Collapsed;
        SizeTip.Visibility = Visibility.Collapsed;
        Toolbar.Visibility = Visibility.Collapsed;
        foreach (Rectangle handle in _handles) handle.Visibility = Visibility.Collapsed;
    }

    private static void SetRect(FrameworkElement element, double x, double y, double w, double h)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = w;
        element.Height = h;
    }

    /// <summary>内容自适应元素的测量：清掉旧的显式宽高再量。
    /// 显式 Width/Height 会作为约束钳制 Measure 结果——不清掉的话，
    /// 尺寸永远量出旧值（工具栏第二行显示不出来就是这个坑）。</summary>
    private static void AutoMeasure(FrameworkElement element)
    {
        element.Width = double.NaN;
        element.Height = double.NaN;
        element.InvalidateMeasure();
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    }

    /// <summary>只摆位置，尺寸交给内容自适应。</summary>
    private static void Position(FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
    }

    #endregion
}

/// <summary>
/// 标注预览层：把 AnnotationPainter 的输出（选区相对物理坐标）经矩阵变换
/// 摆进所在遮罩的 DIP 画布。不参与命中测试，输入全部走窗口 Preview 路由。
/// </summary>
internal sealed class AnnotationLayer : FrameworkElement
{
    private OverlayWindow? _host;

    internal void Attach(OverlayWindow host) => _host = host;

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_host is not { } host)
            return;
        Rect selPhys = host.Session.Selection;
        if (selPhys.IsEmpty || selPhys.Width <= 0)
            return;
        if (!host.Annotations.HasItems && host.DraftAnnotation == null)
            return;

        IEnumerable<Annotation> items = host.DraftAnnotation is { } draft
            ? host.Annotations.Items.Append(draft)
            : host.Annotations.Items;

        double s = host.DpiScale;
        var transform = new Matrix(
            1 / s, 0, 0, 1 / s,
            (selPhys.X - host.MonitorPhys.X) / s,
            (selPhys.Y - host.MonitorPhys.Y) / s);
        drawingContext.PushTransform(new MatrixTransform(transform));
        AnnotationPainter.Draw(drawingContext, items, new Size(selPhys.Width, selPhys.Height));
        drawingContext.Pop();
    }
}
