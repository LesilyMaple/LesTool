using System.Windows;
using System.Windows.Controls;
using LesTool.Platform;

namespace LesTool.Plugin.Preview;

/// <summary>
/// 空格预览插件入口（QuickLook 式）：资源管理器中按空格弹出预览浮窗，再按空格或 Esc 收起。
/// 空格拦截走 Platform 的 KeyboardEvents 条件吞键钩子（订阅驱动安装），
/// 选中项探测走 Platform 的 ExplorerSelection（宿主命令上下文 SelectedFiles 同源）。
/// 自触发功能不走命令注册（2026-10-27 拍板）：触发=内部空格监听，配置归本插件设置页。
/// </summary>
public sealed class PreviewPlugin : ILesPlugin
{
    private SpacePreviewController? _controller;

    public string Id => "preview";

    public void Initialize(ILesHost host)
    {
        _controller = new SpacePreviewController(host.Settings, msg => host.Toast(msg), () => host.CanDispatch());
        host.OwnCleanup(() => _controller?.Shutdown());
        _controller.Initialize();

        host.RegisterSettingsPage(BuildPage);
    }

    public void Shutdown() => _controller?.Shutdown();

    private FrameworkElement BuildPage()
    {
        // PLUGIN_UI_SPEC：纯内容 = SettingsSection 堆叠；页框（页头/滚动/边距）宿主管。
        // 详细支持格式与已知降级写进区块说明（清单简介只留一句话门面）。
        var rows = new StackPanel();

        var enabled = new Platform.Controls.CheckRow
        {
            Text = "启用空格预览（全局空格拦截，仅资源管理器前台生效）",
            IsChecked = _controller!.Enabled,
        };
        enabled.Toggled += (_, _) => _controller.Enabled = enabled.IsChecked == true;
        rows.Children.Add(enabled);

        var follow = new Platform.Controls.CheckRow
        {
            Text = "预览期间跟随选中项变化（300ms 轮询）",
            IsChecked = _controller.FollowSelection,
        };
        follow.Toggled += (_, _) => _controller.FollowSelection = follow.IsChecked == true;
        rows.Children.Add(follow);

        return new StackPanel
        {
            Children =
            {
                new Platform.Controls.SettingsSection
                {
                    Title = "预览行为",
                    Caption = "在资源管理器（含桌面）选中文件后按空格弹出预览，再按空格或 Esc 收起；"
                        + "↑↓ 切换选中文件时预览自动跟随。支持图片（1:1 物理像素、GIF 动画）、文本、"
                        + "视频（自动循环播放、点击暂停）、音频（播放控制卡）、"
                        + "PDF/RTF 与 Office 文档（Excel 表格 / Word 段落视图，优先借用系统原生引擎）、"
                        + "文件夹与通用信息卡；重命名、地址栏等输入场景自动放行空格。",
                    Content = rows,
                },
                new Platform.Controls.SettingsSection
                {
                    Title = "已知降级",
                    Caption = "视频音频依赖系统解码器（mkv/hevc 等可能提示解不了）；Excel 图表与条件格式、"
                        + "Word 页眉页脚与复杂排版不显示，超长内容只显示开头。",
                },
            },
        };
    }
}
