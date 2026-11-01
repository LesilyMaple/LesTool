using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.ClipboardHistory;

/// <summary>
/// 剪切板历史插件入口：历史数据层 + 搜索浮窗 + 回贴整体随本插件装载。
/// 命令 clipboard.panel 经统一注册表对热键/命令面板/轮盘槽位三路生效；
/// 回贴目标窗口取自命令上下文（面板路径带快照原前台，其余路径临场前台）。
/// </summary>
public sealed class ClipboardPlugin : ILesPlugin
{
    private ClipboardHistory? _history;
    private ClipboardPanelWindow? _window;

    public string Id => "clipboard";

    public void Initialize(ILesHost host)
    {
        _history = new ClipboardHistory(host.Settings);
        host.OwnCleanup(() => _history?.Stop());
        _history.Start();

        _window = new ClipboardPanelWindow(_history, host.Settings);
        host.OwnCleanup(() => _window?.Close());
        _window.Hidden += () =>
        {
            Log.Information("剪切板面板已收起");
            _history.SaveNow();        // 收起即落盘：不赌防抖到期与退出时机
            _history.DropPreviewCache();
        };
        _window.PrepareForFastShow();

        host.RegisterCommand("clipboard.panel", "剪切板历史", "\uE77F",
            ctx => _window?.Toggle(ctx.FocusWindowHandle),
            keywords: "剪切板 剪贴板 clipboard 历史 粘贴 复制",
            optional: ContextKind.Focus); // 选填：无焦点照样开面板，仅收起时不做焦点归还

        host.RegisterSettingsPage(() => new ClipboardSettingsPage(host.Settings, _history));
    }

    public void Shutdown()
    {
        _window?.Close();
        _history?.Stop(); // 退订广播 + 落盘
    }
}
