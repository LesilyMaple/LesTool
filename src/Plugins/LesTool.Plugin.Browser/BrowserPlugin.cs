using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.Browser;

/// <summary>
/// 浏览器操作集合插件：一个插件注册 7 条命令（回到顶部/跳到底部/上一个/下一个标签/
/// 刷新/恢复关闭标签/新建标签），实现是向前台窗口合成对应快捷键
/// （KeyboardEvents.SendChord，注入带标记不会被自家钩子拦截）。
/// 不做前台程序识别：按键直接发往当前焦点窗口，快捷键在浏览器之外含义不同
/// （如 Ctrl+T 在 Word 是悬挂缩进），是否触发由使用者自己留意前台是什么。
/// </summary>
public sealed class BrowserPlugin : ILesPlugin
{
    private const uint VkTab = 0x09;
    private const uint VkR = 0x52;
    private const uint VkT = 0x54;
    private const uint VkHome = 0x24;
    private const uint VkEnd = 0x23;

    public string Id => "browser";

    public void Initialize(ILesHost host)
    {
        Register(host, "browser.scroll.top", "浏览器：回到顶部", "\uE70E",
            "浏览器 顶部 顶 回到 滚动 top scroll", ctrl: true, vk: VkHome, extended: true);
        Register(host, "browser.scroll.bottom", "浏览器：跳到底部", "\uE70D",
            "浏览器 底部 底 跳到 滚动 bottom scroll", ctrl: true, vk: VkEnd, extended: true);
        Register(host, "browser.tab.previous", "浏览器：上一个标签", "\uE76B",
            "浏览器 上一个 前一个 左 切换 标签 previous tab", ctrl: true, shift: true, vk: VkTab);
        Register(host, "browser.tab.next", "浏览器：下一个标签", "\uE76C",
            "浏览器 下一个 右 切换 标签 next tab", ctrl: true, vk: VkTab);
        Register(host, "browser.page.refresh", "浏览器：刷新页面", "\uE72C",
            "浏览器 刷新 重载 页面 refresh reload", ctrl: true, vk: VkR);
        Register(host, "browser.tab.restore", "浏览器：恢复关闭的标签", "\uE7A6",
            "浏览器 恢复 重开 撤销 关闭 标签 reopen restore tab", ctrl: true, shift: true, vk: VkT);
        Register(host, "browser.tab.new", "浏览器：新建标签", "\uE710",
            "浏览器 新建 新开 标签 new tab", ctrl: true, vk: VkT);

        Log.Information("浏览器操作插件已装载：{Count} 条命令", 7);
    }

    private static void Register(ILesHost host, string id, string name, string glyph, string keywords,
        bool ctrl = false, bool shift = false, uint vk = 0, bool extended = false)
    {
        if (host is not IAsyncCommandHost asyncHost) throw new InvalidOperationException("浏览器插件需要异步命令能力");
        asyncHost.RegisterAsyncCommand(new(id, name, glyph, keywords, Optional: ContextKind.Focus), async (context, token) =>
        {
            var target = WindowTarget.FromHandle(context.FocusWindowHandle);
            // Palette hide animations must finish before keyboard input is sent.
            if (context.Source == TriggerSource.CommandPalette)
                for (var n = 0; n < 12 && !target.IsForeground; n++) await Task.Delay(20, token);
            if (!target.IsForeground) { host.Toast("目标窗口已变化，浏览器操作已取消"); return; }
            KeyboardEvents.SendChord(ctrl, shift, false, vk, extended);
            Log.Information("浏览器操作完成：{Name}", name);
        });
    }

    public void Shutdown()
    {
        // 无常驻资源：命令随注册表生命周期，无需清理
    }
}
