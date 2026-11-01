using System.IO;
using System.Text.Json;
using Serilog;
using LesTool.Platform.Storage;
using System.Text.Json.Serialization;

namespace LesTool.Services.Plugins;

/// <summary>
/// 宿主侧插件用户偏好：%APPDATA%\LesTool\plugins.json——禁用名单 + 图标覆盖。
/// 刻意不进各插件的 settings.json，也不进 plugin.json（作者地盘）——用户的
/// 选择不能被插件升级覆盖（两本账模型：作者账=清单，用户账=本文件）。
/// 读失败按"全部启用、无覆盖"处理；写失败 ERROR（下次启动回弹，须可见）。
/// </summary>
internal static class PluginState
{
    private static JsonSettingsStore<StateData>? _store;
    private static JsonSettingsStore<StateData> Store => _store ??= new(Path.Combine(AppPaths.DataRoot, "plugins.json"), () => new(), validate: value =>
    {
        if (value.IconOverrides is not null && value.IconOverrides.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.IconOverrides.Count)
            throw new InvalidDataException("插件图标配置包含大小写冲突的 Id");
    });
    private static StateData? _data;
    internal static bool IsProtected => Store.IsProtected;
    internal static void ResetProtected() { _data = Store.ResetToDefaults(); Load(); }

    private static HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _iconOverrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>图标覆盖变化（UI 线程）：插件页头、插件管理页订阅即时刷新。</summary>
    internal static event Action? IconOverridesChanged;

    public static bool IsDisabled(string pluginId) => IsProtected || _disabled.Contains(pluginId);

    /// <summary>生效图标 = 用户账覆盖 &gt; 作者账（清单）&gt; 调用方兜底字形。
    /// 导航、插件管理页、插件页头三处统一走这里，保证同一张脸。</summary>
    public static string GetEffectiveIcon(string pluginId, string manifestIcon) =>
        _iconOverrides.TryGetValue(pluginId, out var glyph) && !string.IsNullOrWhiteSpace(glyph)
            ? glyph
            : manifestIcon;

    /// <summary>启动扫描插件目录前调用一次（PluginHost.Load 内）。</summary>
    public static void Load()
    {
        _data = Store.Read();
        _disabled = new(_data.Disabled ?? new(), StringComparer.OrdinalIgnoreCase);
        _iconOverrides = new(_data.IconOverrides ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>置位并立即落盘（UI 线程，插件管理页开关调用；重启生效）。</summary>
    public static void SetEnabled(string pluginId, bool enabled)
    {
        if (enabled) _disabled.Remove(pluginId);
        else _disabled.Add(pluginId);
        Save();
        Log.Information("插件开关：{Id} → {State}（重启生效）", pluginId, enabled ? "启用" : "禁用");
    }

    /// <summary>覆盖插件图标并立即落盘（UI 线程，图标选择器调用；即时生效）。
    /// glyph 传 null = 恢复清单默认。</summary>
    public static void SetIcon(string pluginId, string? glyph)
    {
        if (glyph is null) _iconOverrides.Remove(pluginId);
        else _iconOverrides[pluginId] = glyph;
        Save();
        IconOverridesChanged?.Invoke();
        Log.Information("插件图标覆盖：{Id} → {Glyph}", pluginId, glyph ?? "（恢复默认）");
    }

    private static void Save()
    {
        _data ??= Store.Read();
        _data.Disabled = _disabled.ToList();
        _data.IconOverrides = _iconOverrides.ToDictionary(p => p.Key, p => p.Value);
        Store.Save(_data);
    }

    internal static Task<SaveResult> FlushAsync(CancellationToken token = default) => Store.FlushAsync(token);

    private sealed class StateData
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
        public List<string> Disabled { get; set; } = new();
        public Dictionary<string, string> IconOverrides { get; set; } = new();
    }
}
