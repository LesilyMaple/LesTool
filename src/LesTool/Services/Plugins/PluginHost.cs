using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using LesTool.Platform;
using LesTool.Services;
using Serilog;

namespace LesTool.Services.Plugins;

/// <summary>插件注册的设置页条目（MainWindow 导航"插件"分组消费）。</summary>
internal sealed record PluginPage(string Title, string Glyph, string Key, Func<System.Windows.FrameworkElement> Factory);

/// <summary>
/// 插件装载器：扫描 &lt;exe&gt;/plugins/*/plugin.json → 版本护栏 → 字节数组装载
/// （不锁文件，沿脚本引擎验证过的纪律）→ 反射创建入口 → Initialize。
/// 单插件失败只 ERROR + Toast，不阻断其余插件与宿主。
/// 卸载语义：删除插件目录后重启生效（进程内程序集常驻，与脚本引擎同取舍）。
/// 时机：模块表 "plugins" 步骤（先于热键恢复，插件命令可被绑定）。
/// </summary>
internal static class PluginHost
{
    private sealed record Manifest
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Version { get; init; } = "";
        public string MinPlatform { get; init; } = "1.0.0";
        public string EntryAssembly { get; init; } = "";
        public string EntryType { get; init; } = "";
    }

    private sealed class LoadedPlugin(Manifest manifest, ILesPlugin instance)
    {
        public Manifest Manifest { get; } = manifest;
        public ILesPlugin Instance { get; } = instance;
    }

    private static readonly List<LoadedPlugin> _plugins = new();
    private static readonly List<string> _pluginDirs = new(); // AssemblyResolve 兜底探针用
    private static readonly List<PluginPage> _pages = new();
    private static int _pageSeq;

    /// <summary>设置页清单变化（插件注册时触发，UI 线程）。MainWindow 订阅以重建"插件"导航分组。</summary>
    internal static event Action? PagesChanged;

    internal static IReadOnlyList<PluginPage> Pages => _pages;

    /// <summary>装载全部插件。目录不存在视为零插件（宿主独立可用）。</summary>
    public static void Load()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!Directory.Exists(root))
        {
            Log.Debug("无插件目录，跳过装载：{Root}", root);
            return;
        }

        // 字节装载的程序集解析不走常规探测；挂兜底探针后插件可携带附属 dll
        AppDomain.CurrentDomain.AssemblyResolve += ProbePluginAssemblies;

        foreach (string dir in Directory.EnumerateDirectories(root)
                     .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                LoadOne(dir);
            }
            catch (Exception ex)
            {
                string name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
                Log.Error(ex, "插件装载失败：{Name}", name);
                ToastService.Show($"插件装载失败：{name}，详见日志", 4000);
            }
        }
        Log.Information("插件装载完成：{Count} 个（设置页 {Pages} 页）", _plugins.Count, _pages.Count);
    }

    private static void LoadOne(string dir)
    {
        string manifestPath = Path.Combine(dir, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            Log.Debug("插件目录缺 plugin.json，跳过：{Dir}", dir);
            return;
        }

        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id)
            || string.IsNullOrWhiteSpace(manifest.EntryAssembly) || string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            Log.Warning("插件清单不完整，跳过：{Path}", manifestPath);
            return;
        }

        // 版本护栏：宿主 Platform 契约过旧时拒绝装载并明说，比装载后 MissingMethod 温和
        Version platform = typeof(ILesPlugin).Assembly.GetName().Version ?? new Version();
        if (Version.TryParse(manifest.MinPlatform, out var min) && min > platform)
        {
            Log.Warning("插件要求的 Platform 版号高于宿主（要 {Min}，有 {Platform}）：{Id}",
                manifest.MinPlatform, platform, manifest.Id);
            ToastService.Show($"插件「{manifest.Name}」需要新版宿主，未装载", 4000);
            return;
        }

        string dllPath = Path.Combine(dir, manifest.EntryAssembly);
        if (!File.Exists(dllPath))
        {
            Log.Warning("插件入口程序集不存在，跳过：{Path}", dllPath);
            return;
        }

        var assembly = Assembly.Load(File.ReadAllBytes(dllPath)); // 字节装载：不锁文件，随时可删
        var instance = (ILesPlugin?)Activator.CreateInstance(assembly.GetType(manifest.EntryType) ?? throw new InvalidOperationException($"入口类型缺失：{manifest.EntryType}"));
        if (instance is null)
            throw new InvalidOperationException($"入口类型无法实例化：{manifest.EntryType}");
        if (!string.Equals(instance.Id, manifest.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"清单 Id 与入口 Id 不一致：{manifest.Id} vs {instance.Id}");

        // 字节装载的程序集其原生库（onnxruntime 等）落在插件目录，注册进原生搜索路径
        AddDllDirectory(Path.GetFullPath(dir));

        _pluginDirs.Add(Path.GetFullPath(dir));
        var host = new PluginHostContext(manifest.Id, Path.GetFullPath(dir));
        instance.Initialize(host);
        _plugins.Add(new LoadedPlugin(manifest, instance));
        Log.Information("插件已装载：{Id}（{Name} v{Version}）", manifest.Id, manifest.Name, manifest.Version);
    }

    /// <summary>逆序关停全部插件（先于基础服务，保住"进行中收摊"语义）。单插件失败不阻断其余。</summary>
    public static void ShutdownAll()
    {
        for (var i = _plugins.Count - 1; i >= 0; i--)
        {
            try
            {
                _plugins[i].Instance.Shutdown();
                Log.Debug("插件已关停：{Id}", _plugins[i].Manifest.Id);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "插件关停失败（继续收其余）：{Id}", _plugins[i].Manifest.Id);
            }
        }
    }

    /// <summary>插件附属程序集解析兜底：按程序集简单名在插件目录里找。</summary>
    private static Assembly? ProbePluginAssemblies(object? sender, ResolveEventArgs args)
    {
        string fileName = new AssemblyName(args.Name).Name + ".dll";
        foreach (string dir in _pluginDirs)
        {
            string candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return Assembly.Load(File.ReadAllBytes(candidate));
        }
        return null;
    }

    /// <summary>把插件目录加进原生 DLL 搜索路径（.NET 的 P/Invoke 经
    /// LoadLibraryEx(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS) 解析，AddDllDirectory 的目录参与其中）。
    /// 字节装载不会把插件目录带入原生搜索，不注册则 onnxruntime 这类原生库必然找不到。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string directory);

    /// <summary>单插件的宿主门面实现：命令注册、设置页登记、资产/设置隔离、Toast。</summary>
    private sealed class PluginHostContext(string pluginId, string pluginDir) : ILesHost
    {
        public IPluginAssets Assets { get; } = new PluginAssets(Path.Combine(pluginDir, "assets"));

        public IPluginSettings Settings { get; } = new PluginSettings(pluginId);

        public void RegisterCommand(string id, string name, string glyph, Action<LesContext> callback,
            string keywords = "", ContextKind requires = ContextKind.None) =>
            ActionRegistry.Register(id, name, glyph, callback,
                CommandKind.Plugin, keywords, requires);

        public void RegisterSettingsPage(string title, string glyph, Func<System.Windows.FrameworkElement> factory)
        {
            string key = $"plugin:{pluginId}:{_pageSeq++}";
            _pages.Add(new PluginPage(title, glyph, key, factory));
            PagesChanged?.Invoke();
        }

        public void Toast(string message, int durationMs = 2000) =>
            ToastService.Show(message, durationMs);
    }
}
