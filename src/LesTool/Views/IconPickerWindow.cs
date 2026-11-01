using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LesTool.Platform.Controls;

namespace LesTool.Views;

/// <summary>
/// 插件图标选择器（PLUGIN_UI_SPEC F）：Segoe MDL2 官方文档常用字形精选库，
/// 点选即换、即时生效（写用户账，插件升级不覆盖）；「恢复默认图标」清除覆盖回清单。
/// Pick 返回约定：null = 取消；"" = 恢复默认；其余 = 选中的字形字符串。
/// 常规窗口一档（DESIGN.md）：LesWindowStyle + CaptionBar，主题色随皮肤。
/// </summary>
public sealed class IconPickerWindow : Window
{
    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");

    /// <summary>精选字形（官方文档码位，2026-10-31 校对）。覆盖现有插件清单全部图标，
    /// 按语义粗分组排列；图标体系是 MDL2 文本字形，不引入图片资产管线。</summary>
    private static readonly (string Glyph, string Name)[] Glyphs =
    {
        // 通用操作
        ("\uE700", "菜单"), ("\uE721", "搜索"), ("\uE713", "设置"), ("\uE712", "更多"),
        ("\uE710", "添加"), ("\uE711", "取消"), ("\uE738", "移除"), ("\uE74D", "删除"),
        ("\uE74E", "保存"), ("\uE73E", "对勾"), ("\uE7A6", "重做"), ("\uE7A7", "撤销"),
        ("\uE72A", "前进"), ("\uE72B", "后退"), ("\uE72C", "刷新"), ("\uE895", "同步"),
        ("\uE896", "下载"), ("\uE898", "上传"), ("\uE7AC", "打开方式"),
        // 编辑与文件
        ("\uE70F", "编辑"), ("\uE8C6", "剪切"), ("\uE8C8", "复制"), ("\uE77F", "粘贴"),
        ("\uE8AC", "重命名"), ("\uE7C3", "页面"), ("\uE8A5", "文档"), ("\uE8A6", "受保护文档"),
        ("\uE8B7", "文件夹"), ("\uE838", "打开文件夹"), ("\uE8F4", "新建文件夹"),
        ("\uE8E5", "打开文件"), ("\uE8A7", "新窗口打开"),
        // 通信与人
        ("\uE715", "邮件"), ("\uE7BD", "消息"), ("\uE724", "发送"), ("\uE71B", "链接"),
        ("\uE8A1", "链接预览"), ("\uE7C1", "旗标"), ("\uE8C9", "重要"), ("\uE716", "人们"),
        ("\uE77B", "联系人"), ("\uE8F2", "聊天"), ("\uE97A", "回复"),
        // 媒体
        ("\uE722", "相机"), ("\uE8B9", "图片"), ("\uE91B", "照片"), ("\uE714", "视频"),
        ("\uE8B2", "电影"), ("\uE8D6", "音频"), ("\uE90B", "音乐信息"), ("\uE768", "播放"),
        ("\uE769", "暂停"), ("\uE71A", "停止"), ("\uE7C8", "录制"), ("\uE892", "上一曲"),
        ("\uE893", "下一曲"), ("\uE8B1", "随机播放"), ("\uE8EE", "全部循环"),
        ("\uE8ED", "单曲循环"), ("\uE9E9", "均衡器"), ("\uE767", "音量"), ("\uE74F", "静音"),
        // 系统与设备
        ("\uE770", "系统"), ("\uE771", "个性化"), ("\uE772", "设备"), ("\uE773", "搜索与应用"),
        ("\uE774", "网络"), ("\uE775", "时间和语言"), ("\uE776", "轻松使用"), ("\uE777", "更新还原"),
        ("\uE7F4", "显示器"), ("\uE7F5", "音箱"), ("\uE7F6", "耳机"), ("\uE7EE", "其他用户"),
        ("\uE7EF", "管理员"), ("\uE962", "鼠标"), ("\uE961", "输入"), ("\uE92E", "键盘"),
        ("\uE756", "命令提示符"), ("\uE943", "代码"), ("\uE8EF", "计算器"), ("\uE928", "指纹"),
        ("\uE8FE", "扫描"),
        // 网络与云
        ("\uE701", "Wi-Fi"), ("\uE702", "蓝牙"), ("\uE703", "连接"), ("\uE704", "共享网络"),
        ("\uE705", "VPN"), ("\uE753", "云"), ("\uE88E", "USB"), ("\uE909", "全球"),
        // 状态与标记
        ("\uE783", "错误"), ("\uE7BA", "警告"), ("\uE946", "信息"), ("\uE930", "完成"),
        ("\uE734", "收藏"), ("\uE735", "收藏填充"), ("\uE8E1", "点赞"), ("\uE72E", "锁定"),
        ("\uE785", "解锁"), ("\uE718", "图钉"), ("\uE840", "已固定"), ("\uE790", "颜色"),
        ("\uE793", "灯光"), ("\uE945", "闪电"),
        // 时间与位置
        ("\uE787", "日历"), ("\uE8BF", "日视图"), ("\uE916", "秒表"), ("\uE81C", "历史"),
        ("\uE823", "最近"), ("\uE707", "地图定位"), ("\uE81D", "位置"), ("\uE80F", "主页"),
        // 生活
        ("\uE7BF", "购物车"), ("\uE7B8", "包裹"), ("\uE825", "银行"), ("\uE804", "汽车"),
        ("\uE806", "公交"), ("\uE7FC", "游戏"), ("\uE990", "Xbox"), ("\uE99A", "机器人"),
        ("\uE8BE", "叶子"), ("\uE8D7", "权限"), ("\uE8D2", "字体"),
    };

    private string? _result;

    /// <summary>打开选择器。返回 null=取消、""=恢复默认、其余=选中字形。</summary>
    public static string? Pick(Window? owner, string currentGlyph)
    {
        var window = new IconPickerWindow(currentGlyph) { Owner = owner };
        // Owner 经初始化器赋值在 ctor 之后，居中方式只能在这边定
        window.WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        window.ShowDialog();
        return window._result;
    }

    private IconPickerWindow(string currentGlyph)
    {
        Title = "选择插件图标";
        Style = (Style)TryFindResource("LesWindowStyle");
        Width = 560;
        Height = 640;
        MinWidth = 400;
        MinHeight = 320;
        ShowInTaskbar = false;

        var caption = new CaptionBar();
        DockPanel.SetDock(caption, Dock.Top);

        var hint = new TextBlock
        {
            Text = "点选即换，即时生效（图标偏好独立保存，插件升级不覆盖）。",
            Margin = new Thickness(24, 16, 24, 12),
            TextWrapping = TextWrapping.Wrap,
        };
        hint.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextHint");
        DockPanel.SetDock(hint, Dock.Top);

        var reset = new Button
        {
            Content = "恢复默认图标",
            MinWidth = 120,
            Margin = new Thickness(24, 12, 24, 24),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "清除图标覆盖，回到插件清单自带的图标",
        };
        reset.SetResourceReference(Button.ForegroundProperty, "DangerBrush");
        reset.Click += (_, _) => { _result = ""; Close(); };
        DockPanel.SetDock(reset, Dock.Bottom);

        var wrap = new WrapPanel { Margin = new Thickness(24, 0, 24, 0) };
        foreach (var (glyph, name) in Glyphs)
            wrap.Children.Add(BuildGlyphButton(glyph, name, currentGlyph));

        var list = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = wrap };

        var body = new DockPanel();
        body.Children.Add(caption);
        body.Children.Add(hint);
        body.Children.Add(reset);
        body.Children.Add(list);
        Content = body;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(); // Esc = 取消（不改动）
        };
    }

    private Button BuildGlyphButton(string glyph, string name, string currentGlyph)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 20 };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var button = new Button
        {
            Width = 44,
            Height = 44,
            Margin = new Thickness(0, 0, 8, 8),
            Content = icon,
            ToolTip = name,
            Cursor = Cursors.Hand,
        };
        if (glyph == currentGlyph)
        {
            // 当前生效字形给 2px 强调环，一眼定位（选中态用 NavAccent，符合强调色纪律）
            button.BorderThickness = new Thickness(2);
            button.SetResourceReference(Border.BorderBrushProperty, "NavAccent");
        }
        button.Click += (_, _) => { _result = glyph; Close(); };
        return button;
    }
}
