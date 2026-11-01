using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 标注绘制。预览（遮罩画布）与导出（合成到裁剪图）共用这一份代码，
/// 保证所见即所得。绘制坐标系是"相对选区左上角的物理像素"，
/// 调用方负责用 PushTransform 摆到目标空间（预览按 DPI 缩放，导出 1:1）。
/// </summary>
internal static class AnnotationPainter
{
    private static readonly Typeface TextTypeface = new(
        new FontFamily("Microsoft YaHei UI, Segoe UI, Microsoft YaHei"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static void Draw(DrawingContext dc, IEnumerable<Annotation> annotations, Size selectionPx)
    {
        dc.PushClip(new RectangleGeometry(new Rect(new Point(0, 0), selectionPx)));
        foreach (Annotation annotation in annotations)
            DrawOne(dc, annotation);
        dc.Pop();
    }

    private static void DrawOne(DrawingContext dc, Annotation a)
    {
        var brush = new SolidColorBrush(a.Color);
        var pen = new Pen(brush, a.Thickness);
        pen.Freeze();

        switch (a.Tool)
        {
            case AnnotationTool.Rect:
                dc.DrawRectangle(null, pen, a.BoundsRel);
                break;

            case AnnotationTool.Ellipse:
                dc.DrawEllipse(null, pen,
                    new Point((a.BoundsRel.Left + a.BoundsRel.Right) / 2,
                              (a.BoundsRel.Top + a.BoundsRel.Bottom) / 2),
                    a.BoundsRel.Width / 2, a.BoundsRel.Height / 2);
                break;

            case AnnotationTool.Arrow:
                DrawArrow(dc, pen, brush, a.StartRel, a.EndRel);
                break;

            case AnnotationTool.Pen:
                if (a.Points is { Count: >= 2 })
                {
                    var geometry = new StreamGeometry();
                    using (StreamGeometryContext ctx = geometry.Open())
                    {
                        ctx.BeginFigure(a.Points[0], false, false);
                        ctx.PolyLineTo(a.Points.Skip(1).ToList(), true, true);
                    }
                    geometry.Freeze();
                    var roundPen = new Pen(brush, a.Thickness)
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round,
                        LineJoin = PenLineJoin.Round,
                    };
                    roundPen.Freeze();
                    dc.DrawGeometry(null, roundPen, geometry);
                }
                break;

            case AnnotationTool.Text:
                if (!string.IsNullOrEmpty(a.Text))
                {
                    var text = new FormattedText(a.Text, CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, TextTypeface, a.FontSizePx, brush, 1.0);
                    dc.DrawText(text, a.PosRel);
                }
                break;

            case AnnotationTool.Mosaic:
                if (a.Mosaic is { } mosaic)
                    dc.DrawImage(mosaic, a.BoundsRel);
                break;

            case AnnotationTool.Badge:
            {
                // 序号徽章：实心圆 + 居中数字，数字颜色按底色亮度自动取黑白
                double d = BadgeDiameter(a.Thickness);
                dc.DrawEllipse(new SolidColorBrush(a.Color), null, a.PosRel, d / 2, d / 2);
                Brush ink = Luminance(a.Color) > 140 ? Brushes.Black : Brushes.White;
                var number = new FormattedText(a.Number.ToString(), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, TextTypeface, d * 0.6, ink, 1.0);
                dc.DrawText(number, new Point(a.PosRel.X - number.Width / 2, a.PosRel.Y - number.Height / 2));
                break;
            }
        }
    }

    /// <summary>序号徽章直径（物理像素），随粗细滑条联动。</summary>
    public static double BadgeDiameter(double thickness) => 14 + thickness * 3;

    private static double Luminance(Color c) => c.R * 0.299 + c.G * 0.587 + c.B * 0.114;

    /// <summary>箭头：线段画到箭头底部，实心三角头，大小与线宽联动。</summary>
    private static void DrawArrow(DrawingContext dc, Pen pen, Brush brush, Point start, Point tip)
    {
        Vector dir = tip - start;
        double length = dir.Length;
        if (length < 1) return;
        Vector unit = dir / length;
        double headLength = Math.Min(length, Math.Max(12, pen.Thickness * 3.5));
        Point headBase = tip - unit * headLength;
        Vector perp = new(-unit.Y, unit.X);
        double halfWidth = headLength * 0.32;

        var head = new StreamGeometry();
        using (StreamGeometryContext ctx = head.Open())
        {
            ctx.BeginFigure(tip, true, true);
            ctx.LineTo(headBase + perp * halfWidth, true, true);
            ctx.LineTo(headBase - perp * halfWidth, true, true);
        }
        head.Freeze();

        dc.DrawLine(pen, start, headBase);
        dc.DrawGeometry(brush, null, head);
    }
}
