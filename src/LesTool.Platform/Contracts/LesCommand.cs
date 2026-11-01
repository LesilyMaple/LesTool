namespace LesTool.Platform;

/// <summary>命令来源：内置功能 / 用户脚本 / 插件 / 打开操作面板。</summary>
public enum CommandKind
{
    Builtin,
    Script,
    Plugin,
    Panel,
}

/// <summary>
/// 命令声明。宿主的全局命令注册表（ActionRegistry）以它为条目；
/// 所有触发源（热键/命令面板/托盘，将来轮盘槽位）共用一张清单。
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

    public ContextKind Requires { get; }

    public Action<LesContext> Callback { get; }

    public LesCommand(string id, string name, string glyph, string keywords,
        CommandKind kind, ContextKind requires, Action<LesContext> callback)
    {
        Id = id;
        Name = name;
        Glyph = glyph;
        Keywords = keywords;
        Kind = kind;
        Requires = requires;
        Callback = callback;
    }
}
