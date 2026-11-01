using LesTool.Platform;

namespace LesTool.Services;

/// <summary>
/// 入口能力表（2026-10-26/31 拍板）：各触发入口能提供哪些上下文。
/// 轮盘/快捷键=全量；命令面板=除选中项——面板弹出即抢走焦点，
/// SelectedFiles/SelectedText 结构性取不到（原 Searchable 的 SelectedText
/// 特判 2026-10-31 收编进此表）。绑定类 UI 的清单过滤与注册期矛盾检测
/// 共用这一处，勿在别处另立规则（显示期可见性探测另见 RadialWindowController）。
/// </summary>
internal static class ScenarioCapabilities
{
    private const ContextKind AllKinds =
        ContextKind.SelectedText | ContextKind.Clipboard | ContextKind.Cursor
        | ContextKind.Focus | ContextKind.SelectedFiles;

    private static readonly Dictionary<CommandScenarios, ContextKind> _table = new()
    {
        [CommandScenarios.Hotkey] = AllKinds,
        [CommandScenarios.Radial] = AllKinds,
        [CommandScenarios.Palette] = AllKinds & ~(ContextKind.SelectedFiles | ContextKind.SelectedText),
    };

    /// <summary>该入口能否供得起全部所声明的上下文（required 为 flags，须整组满足）。</summary>
    public static bool Provides(CommandScenarios scenario, ContextKind required)
        => _table.TryGetValue(scenario, out var caps) && (caps & required) == required;
}
