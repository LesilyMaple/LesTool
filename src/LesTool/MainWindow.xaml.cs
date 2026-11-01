using System.Windows;
using System.Windows.Controls;
using LesTool.Platform;
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
        ApplyDefaultOrRememberedSize();
        NavPanel.IsChecked = true; // 默认落在命令面板页（触发场景组第一项）
        PluginHost.PagesChanged += RebuildPluginNav; // 插件在主窗口之后装载，注册设置页时动态补导航
        RebuildPluginNav(); // 先建一次：分组常驻，不等插件注册事件（全禁用/零插件时管理页也要可达）
    }

    /// <summary>启动尺寸：上次关闭记住的尺寸优先；首次启动按屏幕物理像素 2323×1483 折算。
    /// 记忆值是程序内单位（DIP），换显示器/改缩放后由工作区夹紧兜底，防窗口出屏。</summary>
    private void ApplyDefaultOrRememberedSize()
    {
        var work = SystemParameters.WorkArea;
        var remembered = App.Settings.MainWindow;
        if (remembered is { Width: > 0, Height: > 0 })
        {
            Width = Math.Clamp(remembered.Width, MinWidth, work.Width);
            Height = Math.Clamp(remembered.Height, MinHeight, work.Height);
            Log.Debug("主窗口按记忆尺寸还原：{Width:0}×{Height:0}（DIP）", Width, Height);
            return;
        }

        double scale = GdiCapture.GetMonitorScale(new Point(0, 0));
        Width = Math.Min(2323 / scale, work.Width);
        Height = Math.Min(1483 / scale, work.Height);
        Log.Debug("主窗口按默认尺寸（物理 2323×1483）启动：{Width:0}×{Height:0}（DIP）", Width, Height);
    }

    protected override void OnClosed(EventArgs e)
    {
        // 关闭即记住当前大小（最大化时记还原态尺寸，避免下次启动"伪最大化"）
        var size = WindowState == WindowState.Maximized ? RestoreBounds.Size : new Size(ActualWidth, ActualHeight);
        App.Settings.MainWindow = new MainWindowSettings { Width = size.Width, Height = size.Height };
        try { AppSettings.Save(App.Settings); }
        catch (Exception error) { Log.Error(error, "窗口尺寸未保存"); }
        Log.Debug("主窗口尺寸已记忆：{Width:0}×{Height:0}（DIP）", size.Width, size.Height);
        base.OnClosed(e);
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
                "panelcfg" => new PanelSettingsPage(), // 2026-10-31 转正：面板键位归此页（原全局热键页已删）
                "settings" => new SettingsPage(),
                "pluginmanage" => new PluginManagePage(),
                _ => new PlaceholderPage(key, "开发中。"),
            };
        _pages[key] = page;
        return page;
    }

    /// <summary>重建导航"插件"分组：常驻（插件管理 + 各插件设置页）；
    /// 插件注册设置页时触发重建。零插件时分组仍在，管理页负责解释装哪、怎么装。</summary>
    private void RebuildPluginNav()
    {
        foreach (FrameworkElement item in _pluginNav)
            NavMenu.Children.Remove(item);
        _pluginNav.Clear();

        var header = new TextBlock { Text = "插件", Style = (Style)FindResource("GroupHeaderStyle") };
        NavMenu.Children.Add(header);
        _pluginNav.Add(header);

        var manage = new RadioButton
        {
            Style = (Style)FindResource("NavItemStyle"),
            Content = "插件管理",
            Tag = "pluginmanage",
        };
        manage.Checked += OnNavChecked;
        NavMenu.Children.Add(manage);
        _pluginNav.Add(manage);

        foreach (PluginPage pluginPage in PluginHost.Pages)
        {
            var radio = new RadioButton
            {
                Style = (Style)FindResource("NavItemStyle"),
                Content = PluginHost.InfoOf(pluginPage.PluginId)?.Name ?? pluginPage.PluginId,
                Tag = pluginPage.Key,
            };
            radio.Checked += OnNavChecked;
            NavMenu.Children.Add(radio);
            _pluginNav.Add(radio);
        }
        Log.Debug("插件导航分组已重建：管理页 + {Count} 页", PluginHost.Pages.Count);
    }
}
