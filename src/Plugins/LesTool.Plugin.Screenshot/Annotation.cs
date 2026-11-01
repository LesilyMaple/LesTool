using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LesTool.Plugin.Screenshot;

internal enum AnnotationTool { Rect, Ellipse, Arrow, Pen, Text, Mosaic, Badge }

/// <summary>
/// 一条已定稿的标注。坐标全部是"相对选区左上角"的物理像素——
/// 选区整体移动时标注天然跟随；渲染与导出统一裁剪进选区范围。
/// </summary>
internal sealed record Annotation
{
    public required AnnotationTool Tool { get; init; }
    public required Color Color { get; init; }
    public double Thickness { get; init; }                 // 线宽（物理像素，2~10 连续）
    public Rect BoundsRel { get; init; }                  // 矩形/椭圆/马赛克
    public Point StartRel { get; init; }                  // 箭头起点
    public Point EndRel { get; init; }                    // 箭头终点（箭尖）
    public IReadOnlyList<Point>? Points { get; init; }    // 画笔轨迹
    public string? Text { get; init; }                    // 文字内容
    public Point PosRel { get; init; }                    // 文字左上角 / 序号圆心
    public double FontSizePx { get; init; }               // 文字字号（物理像素）
    public int Number { get; init; }                      // 序号徽章的编号
    public BitmapSource? Mosaic { get; init; }            // 马赛克块图（提交时生成）
}

/// <summary>
/// 标注会话状态（仅 UI 线程访问）：当前工具/颜色/粗细、标注栈。
/// 编辑模式与"调整选区"互斥——选中工具即锁定选区，取消选择后标注保留、选区恢复可调。
/// </summary>
internal sealed class AnnotationState
{
    public static readonly Color[] Palette =
    {
        Color.FromRgb(0xE5, 0x39, 0x35), // 红
        Color.FromRgb(0xFD, 0xD8, 0x35), // 黄
        Color.FromRgb(0x43, 0xA0, 0x47), // 绿
        Color.FromRgb(0x1E, 0x88, 0xE5), // 蓝
        Color.FromRgb(0x21, 0x21, 0x21), // 黑
        Color.FromRgb(0xFA, 0xFA, 0xFA), // 白
    };
    public static readonly double MinThickness = 2, MaxThickness = 10;

    /// <summary>粗细滑条值 → 文字工具字号（物理像素）。</summary>
    public static double FontPx(double thickness) => 12 + thickness * 3;

    private readonly List<Annotation> _items = new();
    private readonly List<Annotation> _redo = new();
    private int _badgeCounter;

    public bool EditMode { get; private set; }
    public AnnotationTool? Tool { get; private set; }
    public int ColorIndex { get; private set; }
    public double Thickness { get; private set; } = 4; // 默认中档
    public IReadOnlyList<Annotation> Items => _items;
    public bool HasItems => _items.Count > 0;
    public bool HasRedo => _redo.Count > 0;

    /// <summary>编辑开关、工具、颜色、粗细或标注栈变化。</summary>
    public event Action? Changed;

    public void SetTool(AnnotationTool? tool)
    {
        if (Tool == tool) return;
        Tool = tool;
        EditMode = tool != null; // QQ 模式：无独立编辑开关，选中工具即锁定选区进入编辑，取消选择即退出
        Changed?.Invoke();
    }

    public void SetColor(int index)
    {
        ColorIndex = index;
        Changed?.Invoke();
    }

    public void SetThickness(double thickness)
    {
        // 等值短路：滑条回写与 UI 同步互发时会在此断开
        if (Math.Abs(Thickness - thickness) < 0.01) return;
        Thickness = thickness;
        Changed?.Invoke();
    }

    public void Add(Annotation annotation)
    {
        _items.Add(annotation);
        _redo.Clear(); // 新增后重做历史作废
        Changed?.Invoke();
    }

    /// <summary>撤销最近一条标注。</summary>
    public bool Undo()
    {
        if (_items.Count == 0) return false;
        _redo.Add(_items[^1]);
        _items.RemoveAt(_items.Count - 1);
        Changed?.Invoke();
        return true;
    }

    /// <summary>重做最近撤销的一条。</summary>
    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _items.Add(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        Changed?.Invoke();
        return true;
    }

    /// <summary>下一个序号徽章编号（会话内只增不减，撤销不回收编号）。</summary>
    public int NextBadgeNumber() => ++_badgeCounter;
}
