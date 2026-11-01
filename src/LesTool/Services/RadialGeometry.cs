using System.Windows;
using System.Windows.Media;

namespace LesTool.Services;

/// <summary>
/// 轮盘几何共享数学：悬浮轮盘窗与设置页编辑器共用同一套扇区划分与标签摆位，
/// 保证"编辑器里看到的位置 = 实际轮盘上出现的位置"。
/// 基准尺寸 320 DIP，其余尺寸按比例缩放。约定：扇区 0 = 正上，顺时针编号。
/// 三圈结构（对齐 Quicker 的 24 槽）：
/// - 内圈 0-7、外圈 8-15：可见环带，各 8 个方位；
/// - 扩展圈 16-23：盘面外的不可见区域，覆盖全屏，按方向命中（仅全局场景生效）。
/// 角度以正上为 0、顺时针为正（屏幕系 y 向下）。
/// </summary>
internal static class RadialGeometry
{
    public const double SectorAngle = Math.PI / 4; // 45°

    public const double BaseSize = 320;     // 基准直径（DIP）
    // 比例对齐 Quicker 实测（2026-10-24 像素量图：中央圆 12% / 分界 57% / 外缘 100% 盘面半径）
    public const double BaseCenter = 18;    // 中央信息区半径 12%（以内不选中；只显示动作标题）
    public const double BaseRingSplit = 86; // 内外圈分界半径 57%
    public const double BaseOuter = 150;    // 盘面外缘（以外命中扩展圈）
    public const double BaseInnerLabel = 52;  // 内圈标签半径 ≈ (18+86)/2
    public const double BaseOuterLabel = 118; // 外圈标签半径 ≈ (86+150)/2

    // 编辑器三圈布局（与运行时同比例）：内圈、外圈 + 扩展圈画成可见第三圈；
    // 底盘只铺到 EditorSplit2（扩展圈在运行时位于盘外），最外圈用虚线示意。
    public const double EditorCenter = 13;  // 12% × 108
    public const double EditorSplit1 = 62;  // 57% × 108
    public const double EditorSplit2 = 108;
    public const double EditorOuter = 146;

    public static double Unit(double size) => size / BaseSize;

    /// <summary>环扇几何（窗口坐标系，圆心在 size/2）；innerBase/outerBase 为基准半径（按 size 缩放），
    /// angleIndex 为方位下标 0-7（0=正上、顺时针）。悬浮轮盘与编辑器共用。</summary>
    public static Geometry RingSectorPath(double size, double innerBase, double outerBase, int angleIndex)
    {
        if (angleIndex < 0 || angleIndex >= RadialSettingsData.RingSlots)
            throw new ArgumentOutOfRangeException(nameof(angleIndex));

        double center = size / 2;
        double unit = Unit(size);
        double inner = innerBase * unit;
        double outer = outerBase * unit;
        double from = (angleIndex - 0.5) * SectorAngle;
        double to = (angleIndex + 0.5) * SectorAngle;

        var (ax, ay) = Polar(inner, from);
        var (bx, by) = Polar(outer, from);
        var (cx, cy) = Polar(outer, to);
        var (dx, dy) = Polar(inner, to);

        var figure = new PathFigure { StartPoint = new Point(center + ax, center + ay), IsClosed = true };
        figure.Segments.Add(new LineSegment(new Point(center + bx, center + by), true));
        figure.Segments.Add(new ArcSegment(new Point(center + cx, center + cy),
            new Size(outer, outer), 0, false, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(new Point(center + dx, center + dy), true));
        figure.Segments.Add(new ArcSegment(new Point(center + ax, center + ay),
            new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>可见扇区的环扇几何（悬浮轮盘用）：slot 取 0-15（内圈/外圈）。</summary>
    public static Geometry SectorPath(double size, int slot) =>
        RingSectorPath(size,
            slot < RadialSettingsData.OuterFirst ? BaseCenter : BaseRingSplit,
            slot < RadialSettingsData.OuterFirst ? BaseRingSplit : BaseOuter,
            slot % RadialSettingsData.RingSlots);

    /// <summary>指定半径处的标签中心点（窗口坐标系）；angleIndex 为方位下标 0-7。</summary>
    public static Point LabelCenterAt(double size, double labelBase, int angleIndex)
    {
        var (x, y) = Polar(labelBase * Unit(size), angleIndex * SectorAngle);
        return new Point(size / 2 + x, size / 2 + y);
    }

    /// <summary>槽位标签中心点（窗口坐标系）——扇区角平分线上，内外圈各自半径，与盘面同缩放。</summary>
    public static Point LabelCenter(double size, int slot) =>
        LabelCenterAt(size,
            slot < RadialSettingsData.OuterFirst ? BaseInnerLabel : BaseOuterLabel,
            slot % RadialSettingsData.RingSlots);

    /// <summary>指定半径处的标签可用最大宽度（DIP）：近似弧长，留 10% 余量。</summary>
    public static double LabelMaxWidthAt(double size, double labelBase) =>
        2 * Math.PI * labelBase * Unit(size) / RadialSettingsData.RingSlots * 0.9;

    /// <summary>槽位标签可用最大宽度（DIP）。</summary>
    public static double LabelMaxWidth(double size, int slot) =>
        LabelMaxWidthAt(size, slot < RadialSettingsData.OuterFirst ? BaseInnerLabel : BaseOuterLabel);

    /// <summary>命中：dx/dy 为相对圆心的偏移（DIP）。中央信息区返回 -1；盘外按方向命中
    /// 扩展圈（16-23）；环带内按 45° 分区返回内圈 0-7 / 外圈 8-15。</summary>
    public static int SectorAt(double dx, double dy, double size)
    {
        double unit = Unit(size);
        double radius = Math.Sqrt(dx * dx + dy * dy);
        if (radius < BaseCenter * unit) return -1;

        double angle = Math.Atan2(dx, -dy); // 0=正上、顺时针为正
        if (angle < 0) angle += 2 * Math.PI;
        int direction = (int)(angle / SectorAngle + 0.5) % RadialSettingsData.RingSlots;

        if (radius > BaseOuter * unit) return RadialSettingsData.ExtensionFirst + direction; // 盘外 → 扩展圈
        if (radius < BaseRingSplit * unit) return direction;                                  // 内圈
        return RadialSettingsData.OuterFirst + direction;                                     // 外圈
    }

    /// <summary>极坐标 → 直角坐标偏移（相对圆心；0=正上、顺时针）。</summary>
    private static (double X, double Y) Polar(double radius, double angle) =>
        (radius * Math.Sin(angle), -radius * Math.Cos(angle));
}
