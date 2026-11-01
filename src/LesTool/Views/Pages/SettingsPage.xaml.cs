using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LesTool.Services;

namespace LesTool.Views.Pages;

public partial class SettingsPage : UserControl
{
    private bool _initializing;

    public SettingsPage()
    {
        InitializeComponent();
        if (AppSettings.IsProtected)
            SettingsRoot.Children.Insert(2, new SettingsRecoveryNotice("应用配置需要恢复", AppSettings.ResetProtected));
        // 页面实例被主窗口缓存：每次切回设置页都重新对齐现状（含跟随系统/外部脚本切肤后的变化）
        Loaded += (_, _) => RefreshState();
    }

    private void RefreshState()
    {
        _initializing = true;
        FollowSystemCheck.IsChecked = ThemeManager.FollowSystem;
        DarkOptionsPanel.Children.Clear();
        LightOptionsPanel.Children.Clear();
        foreach (var skin in ThemeManager.Skins)
            BuildSkinCard(skin.IsLight ? LightOptionsPanel : DarkOptionsPanel, skin);
        (LogRetentionManager.Mode switch
        {
            LogRetentionMode.Daily => LogDailyOption,
            LogRetentionMode.Monthly => LogMonthlyOption,
            _ => LogWeeklyOption,
        }).IsChecked = true;
        _initializing = false;
    }

    /// <summary>皮肤选卡：预览色块直接取皮肤字典的真实画刷；选中态 = 所属组当前所选。</summary>
    private void BuildSkinCard(StackPanel panel, SkinInfo skin)
    {
        var selected = skin.IsLight ? ThemeManager.LightSkin : ThemeManager.DarkSkin;
        var card = new RadioButton
        {
            Style = (Style)FindResource("OptionCardStyle"),
            Tag = skin.Id,
            IsChecked = selected == skin.Id,
        };
        card.Checked += OnSkinChecked;
        var content = new StackPanel();
        content.Children.Add(BuildSkinPreview(skin.Id));
        content.Children.Add(new TextBlock
        {
            Text = skin.Name,
            FontSize = (double)FindResource("FontSizeCaption"),
            Foreground = (Brush)FindResource("TextPrimary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        });
        card.Content = content;
        panel.Children.Add(card);
    }

    /// <summary>迷你预览：窗口底/侧栏/卡片/强调色四块（与皮肤实际画刷一致）。</summary>
    private static Border BuildSkinPreview(string id)
    {
        var dict = ThemeManager.LoadSkinDictionary(id);
        Brush Pick(string key, Brush fallback) =>
            dict is not null && dict.Contains(key) ? (Brush)dict[key] : fallback;

        var windowBg = Pick("WindowBackground", Brushes.DimGray);
        var menuBg = Pick("MenuBackground", Brushes.Black);
        var cardBg = Pick("CardBackground", Brushes.Gray);
        var accent = Pick("NavAccent", Brushes.CornflowerBlue);

        var chip = new Border
        {
            Width = 34, Height = 8, Margin = new Thickness(4),
            Background = accent, CornerRadius = new CornerRadius(2),
        };
        var card = new Border { Background = cardBg, Margin = new Thickness(4), Child = chip };
        var side = new Border { Width = 28, Background = menuBg };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(side, 0);
        Grid.SetColumn(card, 1);
        content.Children.Add(side);
        content.Children.Add(card);

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        body.RowDefinitions.Add(new RowDefinition());
        var strip = new Border { Background = cardBg };
        Grid.SetRow(strip, 0);
        Grid.SetRow(content, 1);
        body.Children.Add(strip);
        body.Children.Add(content);

        return new Border
        {
            Width = 112, Height = 64, CornerRadius = new CornerRadius(6),
            Background = windowBg, Child = body,
        };
    }

    private void OnSkinChecked(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        ThemeManager.SetSkin((string)((RadioButton)sender).Tag);
        // 手动选肤会关掉跟随系统：同步开关显示，避免 UI 与状态脱节
        _initializing = true;
        FollowSystemCheck.IsChecked = ThemeManager.FollowSystem;
        _initializing = false;
    }

    private void OnFollowSystemChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        ThemeManager.SetFollowSystem(FollowSystemCheck.IsChecked == true);
    }

    private void OnLogRetentionChecked(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var mode = Enum.Parse<LogRetentionMode>((string)((RadioButton)sender).Tag);
        LogRetentionManager.SetMode(mode);
    }
}
