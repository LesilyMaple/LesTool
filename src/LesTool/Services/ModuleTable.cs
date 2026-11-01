using Serilog;

namespace LesTool.Services;

/// <summary>
/// 启动/退出模块表：把 App 里散装的初始化与关停收成一张显式有序清单，
/// 顺序契约由表序本身承载（改顺序=改表，一眼可审）。
/// 初始化按登记序执行，任一失败即抛（启动期 fail-fast）；
/// 关停按逆序执行（先关依赖别人的：插件/功能先于基础服务），单模块关停失败只记日志。
/// 将来的插件装载器（PluginHost）作为普通模块挂进同一张表，与内置模块同一套纪律。
/// </summary>
internal sealed class ModuleTable
{
    private sealed record Module(string Name, Action Initialize, Action? Shutdown);

    private readonly List<Module> _modules = new();
    private int _initialized = -1; // 已初始化到的下标：关停只回收到这里，未初始化的模块不被"关停"

    public void Add(string name, Action initialize, Action? shutdown = null) =>
        _modules.Add(new Module(name, initialize, shutdown));

    public void InitializeAll()
    {
        for (var i = 0; i < _modules.Count; i++)
        {
            Log.Debug("模块初始化：{Name}", _modules[i].Name);
            _modules[i].Initialize();
            _initialized = i;
        }
        Log.Information("全部 {Count} 个模块就绪", _modules.Count);
    }

    public void ShutdownAll()
    {
        for (var i = _initialized; i >= 0; i--)
        {
            try
            {
                _modules[i].Shutdown?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "模块关停失败（继续收其余）：{Name}", _modules[i].Name);
            }
        }
    }
}
