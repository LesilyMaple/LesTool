using LesTool.Platform;
using Serilog;

namespace LesTool.Services;

/// <summary>
/// 全局命令注册表——所有触发源（热键/命令面板/托盘，将来轮盘槽位与插件装载器）
/// 共用的唯一动作清单。App 启动时登记可用命令（依赖运行期实例的回调闭包在这里注入，
/// 页面不做静态耦合），触发后按 Id 派发，并按 Requires 惰性构建上下文。
/// 新模块加命令只需一行 Register；这个 Register 签名就是将来的公共插件 API
/// （命令模型 LesCommand/LesContext 已上收到 LesTool.Platform 契约程序集）。
/// 派发默认发生在 UI 线程（热键 WM_HOTKEY 即在 UI 线程消息窗口上分发），
/// 上下文构建依赖此约定。注册须在 UI 线程（List/Dictionary 非线程安全）。
/// </summary>
public static class ActionRegistry
{
    private static readonly List<LesCommand> _ordered = new();
    private static readonly Dictionary<string, LesCommand> _byId = new();

    public static IReadOnlyList<LesCommand> All => _ordered;

    public static void Register(string id, string name, string glyph, Action<LesContext> callback,
        CommandKind kind = CommandKind.Builtin, string keywords = "", ContextKind requires = ContextKind.None)
    {
        var command = new LesCommand(id, name, glyph, keywords, kind, requires, callback);
        var index = _ordered.FindIndex(c => c.Id == id);
        if (index >= 0) _ordered[index] = command;
        else _ordered.Add(command);
        _byId[id] = command;
    }

    public static LesCommand? Find(string id) => _byId.TryGetValue(id, out var command) ? command : null;

    public static string NameOf(string id) => Find(id)?.Name ?? id;

    public static void Execute(string id)
    {
        if (!_byId.TryGetValue(id, out var command))
        {
            Log.Warning("未知命令：{Id}", id);
            return;
        }

        // 上下文按声明惰性获取；获取部分失败只置空字段，不阻断派发
        LesContext context = command.Requires == ContextKind.None
            ? LesContext.Empty
            : ContextProvider.Build(command.Requires);

        Log.Information("派发命令：{Id}（来源上下文 {Kind}）", id, command.Requires);
        try
        {
            command.Callback(context);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "命令执行失败：{Id}", id);
        }
    }
}
