namespace LesTool.Platform;

/// <summary>命令来源：内置功能 / 插件 / 打开操作面板。</summary>
public enum CommandKind
{
    Builtin,
    Plugin,
    Panel,
}

/// <summary>
/// 命令声明"可从哪些入口触发"（2026-10-26/31 拍板的双属性之一）。
/// 绑定类 UI（热键区/命令面板/轮盘槽位）按它过滤；缺省 All=存量兼容。
/// 用户可见的配置维度只有场景；上下文声明（必填/选填）永不进 UI，纯代码契约。
/// </summary>
[Flags]
public enum CommandScenarios
{
    None = 0,
    Hotkey = 1 << 0,
    Palette = 1 << 1,
    Radial = 1 << 2,
    All = Hotkey | Palette | Radial,
}

/// <summary>
/// 命令声明。宿主的全局命令注册表（ActionRegistry）以它为条目；
/// 所有触发源（热键/命令面板/轮盘槽位）共用一张清单。
/// 双属性：Scenarios=支持哪些入口；Requires/Optional=上下文需求——
/// 必填缺了动作无意义（绑定期按能力表门禁、运行时空值平台拦截），
/// 选填缺了降级不失效（空字段照常派发，动作自判）。
/// </summary>
public sealed class LesCommand
{
    public string Id { get; }
    public string Name { get; }

    /// <summary>Segoe MDL2 Assets 字形码位，动作选择器与命令面板展示用。</summary>
    public string Glyph { get; }

    /// <summary>命令面板搜索用：空格分隔的别名（中英文皆可）。</summary>
    public string Keywords { get; }

    public CommandKind Kind { get; }

    public CommandScenarios Scenarios { get; }

    /// <summary>必填上下文：缺了动作无意义。</summary>
    public ContextKind Requires { get; }

    /// <summary>选填上下文：缺了降级不失效。</summary>
    public ContextKind Optional { get; }

    public Action<LesContext> Callback { get; }

    public LesCommand(string id, string name, string glyph, string keywords,
        CommandKind kind, CommandScenarios scenarios, ContextKind requires, ContextKind optional,
        Action<LesContext> callback)
    {
        Id = id;
        Name = name;
        Glyph = glyph;
        Keywords = keywords;
        Kind = kind;
        Scenarios = scenarios;
        Requires = requires;
        Optional = optional;
        Callback = callback;
    }
}
