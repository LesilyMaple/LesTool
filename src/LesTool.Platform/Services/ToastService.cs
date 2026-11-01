using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Serilog;

namespace LesTool.Platform;

/// <summary>
/// 右下角轻量 Toast。任意线程可调（内部封送 UI 线程），到时淡出自毁；
/// 多条同时在屏时自下而上按 56px 估算槽位堆叠。样式全部取主题资源，随 Dark/Light 切换。
/// 平台服务：宿主与插件共用（插件经 ILesHost.Toast 间接触达）。
/// </summary>
public static class ToastService
{
    private const double SlotHeight = 56; // 槽位估算高度（实际略小，留缝隙）
    private const double Margin = 16;

    private static int _activeCount;

    public static void Show(string message, int durationMs = 2000)
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() => CreateAndShow(message, durationMs));
    }

    private static void CreateAndShow(string message, int durationMs)
    {
        try
        {
            var text = new TextBlock
            {
                Text = message,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 420,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            text.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeBody");

            var border = new Border
            {
                Child = text,
                Padding = new Thickness(16, 10, 16, 10),
                BorderThickness = new Thickness(1),
            };
            border.SetResourceReference(Border.BackgroundProperty, "MenuBackground");
            border.SetResourceReference(Border.BorderBrushProperty, "MenuBorderColor");

            // AllowsTransparency + 无边框 + 不抢焦点 + 不进任务栏：弹出对使用者零干扰
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Focusable = false,
                IsHitTestVisible = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = border,
            };

            var slot = _activeCount++;
            window.Closed += (_, _) => _activeCount = Math.Max(0, _activeCount - 1);

            // SizeToContent 要 Show 后才知道尺寸，Loaded 里按工作区定位
            window.Loaded += (_, _) =>
            {
                var area = SystemParameters.WorkArea;
                window.Left = area.Right - window.ActualWidth - Margin;
                window.Top = area.Bottom - window.ActualHeight - Margin - slot * SlotHeight;
            };

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
                fade.Completed += (_, _) => window.Close();
                window.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            timer.Start();

            window.Show();
            Log.Debug("Toast 显示：{Message}", message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Toast 显示失败：{Message}", message);
        }
    }
}
