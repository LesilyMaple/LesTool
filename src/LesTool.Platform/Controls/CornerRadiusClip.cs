using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LesTool.Platform.Controls;

/// <summary>
/// 让 Border 按 CornerRadius 裁剪子内容。WPF 原生 CornerRadius 只影响自身背景/边框绘制，
/// 不裁剪子元素；ClipToBounds 也只按直角矩形裁。挂在 Border 上设 Enabled=True 即可，
/// 尺寸或圆角变化（如最大化收圆角）时自动重算。
/// 平台控件：宿主与插件窗口共用（LesWindowStyle 经 Design.xaml 引用本类型）。
/// </summary>
public static class CornerRadiusClip
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(CornerRadiusClip),
        new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;

        border.SizeChanged += (_, _) => UpdateClip(border);
        border.Loaded += (_, _) => UpdateClip(border);

        // 最大化等 Trigger 改 CornerRadius 时同步重裁
        var descriptor = DependencyPropertyDescriptor.FromProperty(Border.CornerRadiusProperty, typeof(Border));
        descriptor.AddValueChanged(border, (_, _) => UpdateClip(border));
    }

    private static void UpdateClip(Border border)
    {
        if (border.ActualWidth <= 0 || border.ActualHeight <= 0) return;
        var radius = border.CornerRadius.TopLeft;
        border.Clip = new RectangleGeometry(
            new Rect(0, 0, border.ActualWidth, border.ActualHeight), radius, radius);
    }
}
