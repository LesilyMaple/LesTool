using System.Windows;

namespace LesTool.Platform;

/// <summary>
/// 插件宿主门面：插件经此注册命令与设置页、取资产与设置、发提示。
/// 鼠标事件（<see cref="MouseEvents"/>）与轮盘抑制（<see cref="RadialGuard"/>）
/// 是 Platform 公共静态成员，插件直接引用使用，不经本接口绕行。
/// 全部方法 UI 线程调用（插件 Initialize/Shutdown 均在 UI 线程）。
/// </summary>
public interface ILesHost
{
    /// <summary>登记一条命令（来源记为 Plugin）。命令 Id 全局唯一，
    /// 约定以插件 Id 为前缀（如 volume.mixer），重复 Id 会拒绝整个插件的注册批次。
    /// 双属性（2026-10-26/31 拍板）：scenarios=可从哪些入口触发（缺省三源全开）；
    /// requires=必填上下文（缺了动作无意义，绑定期按入口能力表门禁、运行时空值平台拦截）；
    /// optional=选填上下文（缺了降级不失效）。登记后各触发源按声明自动可用。</summary>
    void RegisterCommand(string id, string name, string glyph, Action<LesContext> callback,
        string keywords = "", ContextKind requires = ContextKind.None,
        CommandScenarios scenarios = CommandScenarios.All, ContextKind optional = ContextKind.None);

    /// <summary>登记设置页（可选，可多次）：进主窗口导航"插件"分组，工厂每次建页时调用。
    /// 工厂只返回"纯内容"（一串 <see cref="Controls.SettingsSection"/>）；页框（滚动/边距/
    /// 页头/热键区）由宿主统一包装（PLUGIN_UI_SPEC）：页头 = 图标（可点击换）+ 名称 +
    /// 清单简介，热键配置自动注入在页头；导航名与图标取清单 + 用户覆盖，不由插件自报。</summary>
    void RegisterSettingsPage(Func<FrameworkElement> factory);

    /// <summary>本插件的资产解析（根目录 plugins/&lt;id&gt;/assets/）。</summary>
    IPluginAssets Assets { get; }

    /// <summary>本插件独立设置（%APPDATA%\LesTool\plugins\&lt;id&gt;\settings.json）。</summary>
    IPluginSettings Settings { get; }

    /// <summary>右下角 Toast 提示（线程安全，内部封送 UI 线程）。</summary>
    void Toast(string message, int durationMs = 2000);
}

/// <summary>
/// 插件资产访问（根目录 plugins/&lt;id&gt;/assets/）。
/// 资产三原则（对所有插件生效）：装载不依赖任何资产；子功能用资产时自查、
/// 缺失按子功能独立降级；降级文案写明具体放置路径。
/// </summary>
public interface IPluginAssets
{
    /// <summary>解析 assets/ 下的相对路径；存在返回全路径，不存在返回 null（路径越界同样返回 null）。</summary>
    string? Resolve(string relativePath);

    /// <summary>列 assets/ 下某子目录的文件（返回相对 assets/ 的路径，含子目录）；目录不存在返回空表。</summary>
    IReadOnlyList<string> List(string relativeDir, string pattern = "*");
}

/// <summary>插件独立键值设置，JSON 落盘。键为插件私有命名空间，宿主与其他插件不可见。</summary>
public interface IPluginSettings
{
    T Get<T>(string key, T fallback);
    void Set<T>(string key, T value);
}

/// <summary>
/// 插件入口。装载器（宿主 PluginHost）反射创建后调用；Initialize/Shutdown 均在 UI 线程。
/// Shutdown 须幂等：装载失败、正常退出、异常路径都可能触达。
/// </summary>
public interface ILesPlugin
{
    /// <summary>插件 Id（目录名、设置目录名、命令 Id 前缀约定共用）。</summary>
    string Id { get; }

    /// <summary>装载时调用一次：注册命令/设置页、订阅事件。禁止在此访问磁盘资产（资产三原则一）。</summary>
    void Initialize(ILesHost host);

    /// <summary>退出时调用（模块表逆序，先于基础服务收摊）：停进程、退订阅、放重资源。</summary>
    void Shutdown();
}
