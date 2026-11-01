using System.IO;
using System.Reflection;
using System.Text.Json;
using LesTool.Platform;
using LesTool.Services;
using Serilog;

namespace LesTool.Services.Plugins;

/// <summary>插件注册的设置页条目（MainWindow 导航"插件"分组消费）。
/// 名与图标不随页面走：导航/页头统一从清单 + 用户图标覆盖解析（PLUGIN_UI_SPEC F）。</summary>
internal sealed record PluginPage(string PluginId, string Key, Func<System.Windows.FrameworkElement> Factory);

/// <summary>扫描发现的一个插件条目（含未装载的），插件管理页消费。</summary>
internal enum PluginStatus
{
    /// <summary>本次启动已装载。</summary>
    Loaded,

    /// <summary>被用户禁用（开关重启生效，本次未装载）。</summary>
    Disabled,

    /// <summary>装载失败（清单不完整/缺入口/版本护栏/装载异常），Reason 说明原因。</summary>
    Failed,
}

internal sealed record PluginInfo(string Id, string Name, string Version, string Icon, string Description,
    PluginStatus Status, string? Reason = null);

/// <summary>
/// 插件装载器：扫描 &lt;exe&gt;/plugins/*/plugin.json → 版本护栏 → 独立加载上下文
/// （不锁文件）→ 反射创建入口 → Initialize。
/// 单插件失败只 ERROR + Toast，不阻断其余插件与宿主。
/// 启用开关（PluginState，宿主侧）：禁用的插件跳过装载但仍记入 Discovered，重启生效。
/// 卸载语义：删除插件目录后重启生效（进程内程序集常驻）。
/// 时机：模块表 "plugins" 步骤（先于热键恢复，插件命令可被绑定）。
/// </summary>
internal static class PluginHost
{
    internal sealed record Manifest
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Version { get; init; } = "";
        public string Description { get; init; } = "";
        public string Icon { get; init; } = "\uE713"; // 管理页兜底字形（清单未给时）
        public int ApiContract { get; init; }
        public string MinPlatform { get; init; } = "";
        public string EntryAssembly { get; init; } = "";
        public string EntryType { get; init; } = "";
    }

    private sealed class LoadedPlugin(Manifest manifest, ILesPlugin instance, PluginHostContext context)
    {
        public Manifest Manifest { get; } = manifest;
        public ILesPlugin Instance { get; } = instance;
        public PluginHostContext Context { get; } = context;
    }

    private static readonly List<LoadedPlugin> _plugins = new();
    private static readonly HashSet<string> _seenIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<PluginPage> _pages = new();
    private static readonly List<PluginInfo> _discovered = new(); // 扫描期记录（含禁用与失败）
    private static int _pageSeq;
    private static readonly List<IAsyncPluginSettings> _settingsStores = new();
    internal static readonly Dictionary<string, PluginSettings> ProtectedSettings = new(StringComparer.OrdinalIgnoreCase);

    // plugin.json 用 camelCase，C# 记录属性是 PascalCase——必须显式开大小写不敏感，
    // 否则全部属性落默认值、清单被当"不完整"跳过（P1 埋的雷，2026-10-15 验收炸出）
    private static readonly JsonSerializerOptions ManifestJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>设置页清单变化（插件注册时触发，UI 线程）。MainWindow 订阅以重建"插件"导航分组。</summary>
    internal static event Action? PagesChanged;

    internal static IReadOnlyList<PluginPage> Pages => _pages;
    internal static Assembly AssemblyOf(string id) => _plugins.Single(p => p.Manifest.Id == id).Instance.GetType().Assembly;

    /// <summary>本次启动扫描到的全部插件（含被禁用与装载失败的），插件管理页消费。</summary>
    internal static IReadOnlyList<PluginInfo> Discovered => _discovered;

    /// <summary>按 Id 查扫描信息（名称/版本/简介/图标），导航与页头用；查无返回 null。</summary>
    internal static PluginInfo? InfoOf(string pluginId) =>
        _discovered.FirstOrDefault(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));

    /// <summary>装载全部插件。目录不存在视为零插件（宿主独立可用）。
    /// 用户开关先于扫描加载（PluginState），禁用插件不装载但记入 Discovered。</summary>
    public static void Load() => LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "plugins"));

    internal static void LoadFromDirectory(string root)
    {
        if (!Directory.Exists(root))
        {
            Log.Debug("无插件目录，跳过装载：{Root}", root);
            return;
        }

        PluginState.Load();

        var directories = Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();
        var dependencyErrors = PluginDependencyAudit.Check(directories.Where(IsAuditCandidate));
        foreach (string dir in directories)
        {
            if (dependencyErrors.TryGetValue(dir, out var error))
            {
                _discovered.Add(new(Path.GetFileName(dir), Path.GetFileName(dir), "", "\uE713", "", PluginStatus.Failed, error));
                Log.Error("插件预检失败：{Error}", error);
            }
            else LoadOne(dir);
        }
        Log.Information("插件装载完成：装载 {Loaded}、禁用 {Disabled}、失败 {Failed}（设置页 {Pages} 页）",
            _discovered.Count(d => d.Status == PluginStatus.Loaded),
            _discovered.Count(d => d.Status == PluginStatus.Disabled),
            _discovered.Count(d => d.Status == PluginStatus.Failed),
            _pages.Count);
    }

    private static bool IsAuditCandidate(string directory)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(directory, "plugin.json")), ManifestJson);
            return manifest is not null && !PluginState.IsDisabled(manifest.Id) && manifest.ApiContract == 1
                && string.Equals(manifest.Id, Path.GetFileName(directory), StringComparison.OrdinalIgnoreCase)
                && System.Text.RegularExpressions.Regex.IsMatch(manifest.Id, @"^[a-z0-9][a-z0-9._-]*$")
                && !manifest.Id.Contains("..") && Version.TryParse(manifest.MinPlatform, out var version)
                && version <= typeof(ILesPlugin).Assembly.GetName().Version
                && !string.IsNullOrWhiteSpace(manifest.EntryType)
                && PathBoundary.Contains(directory, Path.GetFullPath(Path.Combine(directory, manifest.EntryAssembly)))
                && File.Exists(Path.Combine(directory, manifest.EntryAssembly));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return false; }
    }

    internal static void LoadOne(string dir)
    {
        string dirName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        ILesPlugin? instance = null;
        PluginHostContext? host = null;
        PluginLoadContext? loadContext = null;
        Manifest? manifest = null;
        try
        {
            string manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                Log.Debug("插件目录缺 plugin.json，跳过：{Dir}", dir);
                _discovered.Add(new PluginInfo(dirName, dirName, "", "\uE713", "", PluginStatus.Failed, "缺少 plugin.json"));
                return;
            }

            manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), ManifestJson);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id)
                || string.IsNullOrWhiteSpace(manifest.EntryAssembly) || string.IsNullOrWhiteSpace(manifest.EntryType))
            {
                Log.Warning("插件清单不完整，跳过：{Path}", manifestPath);
                _discovered.Add(new PluginInfo(dirName, manifest?.Name ?? dirName, manifest?.Version ?? "",
                    manifest?.Icon ?? "\uE713", manifest?.Description ?? "", PluginStatus.Failed, "清单不完整"));
                return;
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.Id, @"^[a-z0-9][a-z0-9._-]*$")
                || manifest.Id.Contains("..") || !string.Equals(dirName, manifest.Id, StringComparison.OrdinalIgnoreCase)
                || !_seenIds.Add(manifest.Id))
                throw new InvalidDataException("插件 Id 无效、重复或与目录名不符：" + manifest.Id);
            if (manifest.ApiContract != 1) throw new InvalidDataException("插件契约版本不受支持，请重编插件");

            // 用户开关（宿主侧状态，重启生效）：禁用即完全不问——不走版本护栏、不装载
            if (PluginState.IsDisabled(manifest.Id))
            {
                Log.Information("插件已被用户禁用，跳过装载：{Id}（{Name} v{Version}）", manifest.Id, manifest.Name, manifest.Version);
                _discovered.Add(new PluginInfo(manifest.Id, manifest.Name, manifest.Version, manifest.Icon,
                    manifest.Description, PluginStatus.Disabled));
                return;
            }

            // 版本护栏：宿主 Platform 契约过旧时拒绝装载并明说，比装载后 MissingMethod 温和
            Version platform = typeof(ILesPlugin).Assembly.GetName().Version ?? new Version();
            if (!Version.TryParse(manifest.MinPlatform, out var min)) throw new InvalidDataException("minPlatform 版本无效");
            if (min > platform)
            {
                Log.Warning("插件要求的 Platform 版号高于宿主（要 {Min}，有 {Platform}）：{Id}",
                    manifest.MinPlatform, platform, manifest.Id);
                ToastService.Show($"插件「{manifest.Name}」需要新版宿主，未装载", 4000);
                _discovered.Add(new PluginInfo(manifest.Id, manifest.Name, manifest.Version, manifest.Icon,
                    manifest.Description, PluginStatus.Failed, $"需要宿主 Platform ≥ {manifest.MinPlatform}"));
                return;
            }

            string dllPath = Path.GetFullPath(Path.Combine(dir, manifest.EntryAssembly));
            if (!PathBoundary.Contains(dir, dllPath)) throw new InvalidDataException("入口程序集越过插件目录");
            if (!File.Exists(dllPath))
            {
                Log.Warning("插件入口程序集不存在，跳过：{Path}", dllPath);
                _discovered.Add(new PluginInfo(manifest.Id, manifest.Name, manifest.Version, manifest.Icon,
                    manifest.Description, PluginStatus.Failed, "入口程序集不存在"));
                return;
            }

            loadContext = new PluginLoadContext(dllPath);
            var assembly = loadContext.LoadEntry(dllPath);
            using var reflection = loadContext.EnterContextualReflection();
            instance = (ILesPlugin?)Activator.CreateInstance(assembly.GetType(manifest.EntryType) ?? throw new InvalidOperationException($"入口类型缺失：{manifest.EntryType}"));
            if (instance is null)
                throw new InvalidOperationException($"入口类型无法实例化：{manifest.EntryType}");
            if (!string.Equals(instance.Id, manifest.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"清单 Id 与入口 Id 不一致：{manifest.Id} vs {instance.Id}");

            host = new PluginHostContext(manifest.Id, Path.GetFullPath(dir), loadContext);
            if (((PluginSettings)host.Settings).IsProtected)
            {
                ProtectedSettings[manifest.Id] = (PluginSettings)host.Settings;
                throw new InvalidDataException("插件设置不可恢复，已保护原文件并暂停插件");
            }
            _settingsStores.Add((IAsyncPluginSettings)host.Settings);
            instance.Initialize(host);
            host.FinalizePages(manifest); // 标准页框装配（页头/热键区注入；命令与页面均已登记，收尾一步）
            host.Runtime.Start();
            ActionRegistry.AddBatch(host.Commands, host.Runtime, host.Handlers, host.Policies);
            _pages.AddRange(host.OwnPages);
            host.Runtime.Activate();
            _plugins.Add(new LoadedPlugin(manifest, instance, host));
            _discovered.Add(new PluginInfo(manifest.Id, manifest.Name, manifest.Version, manifest.Icon,
                manifest.Description, PluginStatus.Loaded));
            NotifyPagesChanged();
            Log.Information("插件已装载：{Id}（{Name} v{Version}）", manifest.Id, manifest.Name, manifest.Version);
        }
        catch (Exception ex)
        {
            using var reflection = loadContext?.EnterContextualReflection();
            host?.Runtime.Quiesce();
            if (host is not null) { ActionRegistry.RemoveBatch(host.Commands); _pages.RemoveAll(p => p.PluginId == host.PluginId); }
            try { instance?.Shutdown(); } catch (Exception cleanup) { Log.Error(cleanup, "失败插件关停异常：{Id}", dirName); }
            finally { host?.Runtime.Dispose(); }
            Log.Error(ex, "插件装载失败：{Name}", dirName);
            ToastService.Show($"插件装载失败：{dirName}，详见日志", 4000);
            _discovered.Add(new PluginInfo(manifest?.Id ?? dirName, manifest?.Name ?? dirName, manifest?.Version ?? "", manifest?.Icon ?? "\uE713", manifest?.Description ?? "", PluginStatus.Failed, ex.Message));
        }
    }

    /// <summary>逆序关停全部插件（先于基础服务，保住"进行中收摊"语义）。单插件失败不阻断其余。</summary>
    public static void QuiesceAll()
    {
        foreach (var plugin in _plugins)
        {
            using var reflection = plugin.Context.LoadContext?.EnterContextualReflection();
            plugin.Context.Runtime.Quiesce();
        }
    }

    public static async Task FlushSettingsAsync(CancellationToken token)
    {
        foreach (var settings in _settingsStores)
        {
            var result = await settings.FlushAsync(token);
            if (!result.Succeeded) Log.Error("插件设置尚未保存：{Error}", result.Error);
        }
    }

    public static async Task ShutdownAllAsync()
    {
        QuiesceAll();
        foreach (var plugin in _plugins.AsEnumerable().Reverse().ToArray())
        {
            using var reflection = plugin.Context.LoadContext?.EnterContextualReflection();
            ActionRegistry.RemoveBatch(plugin.Context.Commands);
            _pages.RemoveAll(page => page.PluginId == plugin.Manifest.Id);
            try
            {
                if (plugin.Instance is IAsyncLesPlugin asyncPlugin) await asyncPlugin.ShutdownAsync(CancellationToken.None);
                else plugin.Instance.Shutdown();
            }
            catch (Exception error) { Log.Error(error, "插件异步关停失败：{Id}", plugin.Manifest.Id); }
            finally { plugin.Context.Runtime.Dispose(); }
        }
        _plugins.Clear();
    }

    public static void ShutdownAll()
    {
        QuiesceAll();
        foreach (var plugin in _plugins.AsEnumerable().Reverse())
        {
            ActionRegistry.RemoveBatch(plugin.Context.Commands);
            _pages.RemoveAll(p => p.PluginId == plugin.Manifest.Id);
            try { plugin.Instance.Shutdown(); }
            catch (Exception ex) { Log.Error(ex, "插件关停失败：{Id}", plugin.Manifest.Id); }
            finally { plugin.Context.Runtime.Dispose(); }
        }
        _plugins.Clear();
    }

    private static void NotifyPagesChanged()
    {
        if (PagesChanged is null) return;
        foreach (Action listener in PagesChanged.GetInvocationList())
            try { listener(); } catch (Exception e) { Log.Error(e, "插件页面通知失败"); }
    }

    /// <summary>单插件的宿主门面实现：命令注册、设置页登记、资产/设置隔离、Toast。</summary>
    internal sealed class PluginHostContext(string pluginId, string pluginDir, PluginLoadContext? loadContext = null) : ILesHost, IPluginLifetimeHost, IAsyncCommandHost
    {
        internal string PluginId => pluginId;
        internal PluginLoadContext? LoadContext => loadContext;
        public PluginRuntime Runtime { get; } = new(pluginId);
        IPluginLifetime IPluginLifetimeHost.Lifetime => Runtime;
        internal List<LesCommand> Commands { get; } = new();
        internal Dictionary<string, CommandConcurrency> Policies { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, Func<LesContext, CancellationToken, Task>> Handlers { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyList<PluginPage> OwnPages => _ownPages;
        private readonly List<string> _commandIds = new();   // 热键区注入判断用
        private readonly List<PluginPage> _ownPages = new(); // 本插件注册的设置页（首页注入热键区）

        public IPluginAssets Assets { get; } = new PluginAssets(Path.Combine(pluginDir, "assets"));

        public IPluginSettings Settings { get; } = new PluginSettings(pluginId);

        public void RegisterCommand(string id, string name, string glyph, Action<LesContext> callback,
            string keywords = "", ContextKind requires = ContextKind.None,
            CommandScenarios scenarios = CommandScenarios.All, ContextKind optional = ContextKind.None)
        {
            if (!id.StartsWith(pluginId + ".", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("命令须使用插件前缀：" + id);
            _commandIds.Add(id);
            Commands.Add(new LesCommand(id, name, glyph, keywords, CommandKind.Plugin, scenarios, requires, optional,
                context => { using var reflection = loadContext?.EnterContextualReflection(); if (Runtime.IsActive) callback(context); }));
        }

        public void RegisterAsyncCommand(CommandDescriptor descriptor, Func<LesContext, CancellationToken, Task> handler)
        {
            RegisterCommand(descriptor.Id, descriptor.Name, descriptor.Glyph,
                _ => throw new InvalidOperationException("异步命令必须经执行器调用"),
                descriptor.Keywords, descriptor.Requires, descriptor.Scenarios, descriptor.Optional);
            Handlers.Add(descriptor.Id, async (context, token) =>
            {
                using var reflection = loadContext?.EnterContextualReflection();
                await handler(context, token);
            });
            Policies.Add(descriptor.Id, descriptor.Concurrency);
        }

        public void RegisterSettingsPage(Func<System.Windows.FrameworkElement> factory)
        {
            string key = $"plugin:{pluginId}:{_pageSeq++}";
            var page = new PluginPage(pluginId, key, () =>
            {
                using var reflection = loadContext?.EnterContextualReflection();
                return factory();
            });
            _ownPages.Add(page);
        }

        /// <summary>设置页标准页框装配（PLUGIN_UI_SPEC A），Initialize 之后调用：
        /// 每个已登记页包上宿主页框（滚动容器 + 页边距 + 最大行宽 860），首页加页头
        /// （图标可点击换 + 清单简介 + 键位小部件——热键配置 2026-10-31 二改迁入页头，
        /// 页尾热键区退役）；无设置页则自动生成"仅页头"页（Browser/Screenshot 类，
        /// 页头即触发配置）。命令按插件 Id 前缀过滤；捕获/冲突/注册注销复用全局链路。</summary>
        public void FinalizePages(Manifest manifest)
        {
            if (_ownPages.Count == 0)
            {
                if (_commandIds.Count == 0) return; // 无命令无页面：不造页
                _ownPages.Add(new PluginPage(pluginId, $"plugin:{pluginId}:hotkeys",
                    () => Scaffold(manifest, withHeader: true, content: null)));
                Log.Debug("已为无设置页插件生成页头页：{Id}", pluginId);
                return;
            }

            for (int i = 0; i < _ownPages.Count; i++)
            {
                var page = _ownPages[i];
                int index = _ownPages.IndexOf(page);
                if (index < 0) continue;
                var original = page.Factory;
                bool first = i == 0;
                _ownPages[index] = page with
                {
                    Factory = () => Scaffold(manifest, first, original),
                };
            }
        }

        /// <summary>宿主页框：ScrollViewer + PagePadding。页头通栏全宽（键位小部件贴
        /// 内容区最右缘，2026-11-01 用户拍板），配置区单独收 860 行宽左对齐保可读性。</summary>
        private static System.Windows.FrameworkElement Scaffold(Manifest manifest, bool withHeader,
            Func<System.Windows.FrameworkElement>? content)
        {
            var page = new System.Windows.Controls.StackPanel();
            page.Margin = System.Windows.Application.Current.TryFindResource("PagePadding") is System.Windows.Thickness pad
                ? pad : new System.Windows.Thickness(32, 24, 32, 24);

            if (withHeader)
                page.Children.Add(new LesTool.Views.Pages.PluginSettingsHeader(
                    manifest.Id, manifest.Name, manifest.Version, manifest.Description, manifest.Icon));
            if (content is not null && content() is { } body)
            {
                var column = new System.Windows.Controls.StackPanel
                {
                    MaxWidth = 860,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                };
                column.Children.Add(body);
                page.Children.Add(column);
            }

            return new System.Windows.Controls.ScrollViewer
            {
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Content = page,
            };
        }

        public void Toast(string message, int durationMs = 2000) =>
            ToastService.Show(message, durationMs);
    }
}
