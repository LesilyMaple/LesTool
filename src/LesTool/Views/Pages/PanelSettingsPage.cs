using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LesTool.Views.Pages;

/// <summary>
/// 命令面板设置页（2026-10-31 转正，替代占位页）：面板自己的键位在此配置。
/// 默认 Alt+Space 由启动期 EnsureLauncherHotkey 写入，可在此改键或再绑第二个键。
/// </summary>
public class PanelSettingsPage : UserControl
{
    public PanelSettingsPage()
    {
        var app = Application.Current;
        var panel = new StackPanel { Margin = new Thickness(36, 32, 36, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = "命令面板",
            FontSize = (double)app.FindResource("FontSizeTitle"),
            Foreground = (Brush)app.FindResource("BrandForeground"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "输入条式启动器：呼出后输入名称或别名过滤，↑↓ 选择、Enter 执行、Esc 关闭；"
                   + "呼出与隐藏是同一个键。默认 Alt+Space，可在下方修改或为它再绑第二个键。",
            FontSize = (double)app.FindResource("FontSizeBody"),
            Foreground = (Brush)app.FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        panel.Children.Add(new HotkeySection("panel."));

        Content = panel;
    }
}
