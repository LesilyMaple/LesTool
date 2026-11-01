using System.Windows;
using System.Windows.Controls;
using LesTool.Services;
using LesTool.Services.Plugins;
using Serilog;
using LesTool.Views.Pages;

namespace LesTool;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly List<FrameworkElement> _pluginNav = new(); // "插件"分组的动态导航项（重建时整体替换）

    public MainWindow()
    {
        InitializeComponent();
        NavRadial.IsChecked = true; // 默认落在轮盘手势页
        PluginHost.PagesChanged += RebuildPluginNav; // 插件在主窗口之后装载，注册设置页时动态补导航
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (PageHost is null) return; // XAML 加载期 IsChecked 触发时 PageHost 尚未就绪
        var key = (string)((RadioButton)sender).Tag;
        Log.Debug("页面切换：{Page}", key);
        PageHost.Content = GetOrCreatePage(key);
    }

    private FrameworkElement GetOrCreatePage(string key)
    {
        if (_pages.TryGetValue(key, out var cached)) return cached;

        FrameworkElement page = key.StartsWith("plugin:", StringComparison.Ordinal)
            ? PluginHost.Pages.FirstOrDefault(p => p.Key == key)?.Factory()
              ?? new PlaceholderPage(key, "插件已卸载或不可用。")
            : key switch
            {
                "radial" => new RadialGesturePage(),
                "panel" => new PanelActionsPage(),
                "hotkey" => new HotkeyPage(),
                "tray" => new TrayPage(),
                "contextmenu" => new PlaceholderPage("右键菜单", "系统右键菜单集成，开发中。"),
                "event" => new PlaceholderPage("事件触发", "监听系统事件自动执行动作，开发中。"),
                "bgaction" => new PlaceholderPage("后台动作", "静默执行的后台动作，开发中。"),
                "settings" => new SettingsPage(),
                _ => new PlaceholderPage(key, "开发中。"),
            };
        _pages[key] = page;
        return page;
    }

    /// <summary>重建导航"插件"分组（插件注册设置页时触发；零插件时分组隐藏）。</summary>
    private void RebuildPluginNav()
    {
        foreach (FrameworkElement item in _pluginNav)
            NavMenu.Children.Remove(item);
        _pluginNav.Clear();

        if (PluginHost.Pages.Count == 0) return;

        var header = new TextBlock { Text = "插件", Style = (Style)FindResource("GroupHeaderStyle") };
        NavMenu.Children.Add(header);
        _pluginNav.Add(header);

        foreach (PluginPage pluginPage in PluginHost.Pages)
        {
            var radio = new RadioButton
            {
                Style = (Style)FindResource("NavItemStyle"),
                Content = pluginPage.Title,
                Tag = pluginPage.Key,
            };
            radio.Checked += OnNavChecked;
            NavMenu.Children.Add(radio);
            _pluginNav.Add(radio);
        }
        Log.Debug("插件导航分组已重建：{Count} 页", PluginHost.Pages.Count);
    }
}
