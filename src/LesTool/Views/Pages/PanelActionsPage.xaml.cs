using System.Windows;
using System.Windows.Controls;
using LesTool.Services;

namespace LesTool.Views.Pages;

public partial class PanelActionsPage : UserControl
{
    public PanelActionsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshAvailability();
    }

    /// <summary>截图已插件化：命令在场才显示入口，缺席时给出可读提示（不显示死按钮）。</summary>
    private void RefreshAvailability()
    {
        bool present = ActionRegistry.Find("screenshot.region") is not null;
        ScreenshotSection.Visibility = present ? Visibility.Visible : Visibility.Collapsed;
        PluginMissingHint.Visibility = present ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>经全局命令注册表派发（与热键/命令面板同一入口，错误处理同源，不再各留一份）。</summary>
    private void OnScreenshotClick(object sender, RoutedEventArgs e) =>
        ActionRegistry.Execute("screenshot.region");
}
