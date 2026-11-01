using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.SmartSearch;

/// <summary>
/// 智能搜索插件入口（2026-10-29 设计拍板，对齐 Quicker 智识的文字路由思路）：
/// 识别选中/输入文字的类型（网址、本机路径、算式、网盘链接、B站号、邮箱、自定义正则），
/// 唯一命中直办、多个命中候选浮窗选、全不中默认引擎兜底。
/// 命令 smartsearch.go 三触发源通用——热键与轮盘路径由宿主取选中文字
/// （UIA → Ctrl+C 兜底 + 剪贴板原样恢复），面板路径与无选中时弹输入条。
/// v1 砍掉（backlog 勿主动提）：OCR 取词、翻译、网盘自动填码、多词一搜、组合引擎。
/// </summary>
public sealed class SmartSearchPlugin : ILesPlugin
{
    private EngineStore? _store;
    private SmartSearchWindow? _window;

    public string Id => "smartsearch";

    public void Initialize(ILesHost host)
    {
        _store = new EngineStore(host.Settings);
        var pipeline = new RecognizerPipeline(_store, host.Settings);
        _window = new SmartSearchWindow(pipeline, host);
        host.OwnCleanup(() => _window?.Close());
        _window.PrepareForFastShow();

        // 场景=热键+轮盘（面板抢焦点取不到选中文字，声明即挡）；选中文本/光标为选填——
        // 无选中时窗口降级为输入条模式（已验收行为，必填会被平台拦截故不可用）
        host.RegisterCommand("smartsearch.go", "智能搜索", "\uE721", ctx =>
            {
                var word = ctx.SelectedText?.Trim();
                Log.Information("智能搜索触发（{Source}，{Mode}）", ctx.Source,
                    string.IsNullOrEmpty(word) ? "无选中 → 输入条" : $"选中 {word.Length} 字");
                _window?.Run(word, ctx.Cursor);
            },
            keywords: "智能搜索 识别 网址 路径 计算 网盘 邮箱 搜索 smartsearch",
            scenarios: CommandScenarios.Hotkey | CommandScenarios.Radial,
            optional: ContextKind.SelectedText | ContextKind.Cursor);

        host.RegisterSettingsPage(() => new SmartSearchSettingsPage(host.Settings, _store!));
    }

    public void Shutdown()
    {
        _window?.Close();
    }
}
