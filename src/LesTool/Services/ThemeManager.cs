using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Serilog;
using LesTool.Platform;
using LesTool.Platform.Controls;

namespace LesTool.Services;

/// <summary>外置皮肤：themes\&lt;id&gt;.xaml（SkinMeta 声明显示名与深浅）。</summary>
public record SkinInfo(string Id, string Name, bool IsLight);

/// <summary>
/// 主题/皮肤管理（2026-10-21 槽位模型）：深色组/浅色组各选一套皮肤 + 独立的「跟随系统」开关。
/// 不跟随时生效 = 手动点选的皮肤；跟随时生效 = 按系统深浅在两组所选间自动切换。
/// 皮肤 = themes\ 磁盘字典（缺键按自身深浅回落内置 Dark/Light 打底），换肤整组替换 App 级
/// 主题字典即时生效；themes\ 目录监听热重载，解析失败记 ERROR + Toast 并保持原样。
/// </summary>
public static class ThemeManager
{
    private const int DwmwaUseImmersiveDarkMode = 20; // build 18985+
    private const int DwmwaUseImmersiveDarkModeLegacy = 19; // 更老的 Win10

    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpFramechanged = 0x0020;

    private const string ThemeLayerKey = "__ThemeLayer"; // 标记运行时换入的主题字典（含皮肤包装层）
    private static readonly Uri DarkUri = new("pack://application:,,,/Themes/Dark.xaml", UriKind.Absolute);
    private static readonly Uri LightUri = new("pack://application:,,,/Themes/Light.xaml", UriKind.Absolute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    private static readonly List<SkinInfo> _skins = new();
    private static FileSystemWatcher? _watcher;
    private static DispatcherTimer? _reloadTimer;
    private static string? _pendingSkinReload;

    public static bool FollowSystem { get; private set; }
    public static string ActiveSkin { get; private set; } = "";
    public static string DarkSkin { get; private set; } = "";
    public static string LightSkin { get; private set; } = "";
    public static IReadOnlyList<SkinInfo> Skins => _skins;

    private static string ThemesDir => Path.Combine(AppContext.BaseDirectory, "themes");

    /// <summary>当前实际呈现是否为浅色（读生效皮肤的 SkinMeta.IsLight）。</summary>
    public static bool IsLightEffective =>
        Application.Current?.TryFindResource("SkinMeta.IsLight") is bool b && b;

    private static bool SystemPrefersLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v ? v != 0 : true;
    }

    /// <summary>启动时调用：迁移旧设置、扫描皮肤、挂监听并应用。须在 Log 配置之后、创建窗口之前。</summary>
    public static void Initialize()
    {
        var s = App.Settings;
        FollowSystem = s.ThemeFollowSystem;

        // 旧值迁移：Dark/Light/System（内置时代）与 "Skin:x"
        var stored = s.Theme;
        if (stored.Equals("System", StringComparison.OrdinalIgnoreCase)) FollowSystem = true;
        if (stored.StartsWith("Skin:", StringComparison.OrdinalIgnoreCase)) stored = stored[5..];
        if (stored.Equals("Dark", StringComparison.OrdinalIgnoreCase)
            || stored.Equals("Light", StringComparison.OrdinalIgnoreCase))
            stored = ""; // 内置主题已退役：按组默认值处理

        ScanSkins();
        DarkSkin = ValidInGroup(s.ThemeDark, light: false) ?? FirstOf(light: false) ?? "";
        LightSkin = ValidInGroup(s.ThemeLight, light: true) ?? FirstOf(light: true) ?? "";

        // 跟随系统：以系统当前深浅为准（换主题常发生在未运行期间，上次生效值会过期）；
        // 手动模式才沿用上次点选
        string active;
        if (FollowSystem && (SystemPrefersLight() ? LightSkin : DarkSkin) is { Length: > 0 } target)
            active = target;
        else
            active = ValidSkin(stored) ? stored : DarkSkin;
        ActiveSkin = active;
        // 旧值迁移后把生效皮肤写回对应槽位，设置页两组各有一张选中卡
        if (ValidInGroup(active, light: true) == active) LightSkin = active;
        else DarkSkin = active;

        Log.Information("主题初始化：跟随系统={Follow}，深色组 {Dark}，浅色组 {Light}，生效 {Active}（呈现 {Eff}）",
            FollowSystem, DarkSkin, LightSkin, ActiveSkin, IsLightEffective ? "浅色" : "深色");
        WatchThemes();
        Apply();
        // 任一窗口（含以后新增）加载即自动同步标题栏深浅，各窗口无需自行调用
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ApplyCaptionTheme((Window)sender)));
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    /// <summary>手动选肤：立即生效并写入所属深/浅槽位（同时关掉跟随系统）。</summary>
    public static void SetSkin(string id)
    {
        var skin = _skins.FirstOrDefault(s => s.Id == id);
        if (skin is null)
        {
            Log.Warning("皮肤不存在或损坏，忽略选择：{Id}", id);
            return;
        }
        Log.Information("切换皮肤：{Old} → {New}（跟随系统关闭）", ActiveSkin, id);
        ActiveSkin = id;
        FollowSystem = false;
        if (skin.IsLight) LightSkin = id; else DarkSkin = id;
        Persist();
        Apply();
    }

    /// <summary>跟随系统开关：开启时立即按系统深浅取对应槽位。</summary>
    public static void SetFollowSystem(bool follow)
    {
        Log.Information("主题跟随系统：{Old} → {New}", FollowSystem, follow);
        FollowSystem = follow;
        if (follow)
        {
            var target = SystemPrefersLight() ? LightSkin : DarkSkin;
            if (target is { Length: > 0 } && target != ActiveSkin) ActiveSkin = target;
        }
        Persist();
        Apply();
    }

    private static void Persist()
    {
        var s = App.Settings;
        s.Theme = ActiveSkin;
        s.ThemeFollowSystem = FollowSystem;
        s.ThemeDark = DarkSkin;
        s.ThemeLight = LightSkin;
        AppSettings.Save(s);
    }

    private static bool ValidSkin(string id) => _skins.Any(s => s.Id == id);

    private static string? ValidInGroup(string id, bool light) =>
        _skins.Any(s => s.Id == id && s.IsLight == light) ? id : null;

    private static string? FirstOf(bool light) => _skins.FirstOrDefault(s => s.IsLight == light)?.Id;

    // ---------- 皮肤清单与字典 ----------

    private static void ScanSkins()
    {
        _skins.Clear();
        try
        {
            if (!Directory.Exists(ThemesDir)) return;
            foreach (var file in Directory.GetFiles(ThemesDir, "*.xaml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var dict = LoadSkinDictionary(id);
                    if (dict is null) continue;
                    var name = dict.Contains("SkinMeta.Name") ? (string)dict["SkinMeta.Name"] : id;
                    var isLight = dict.Contains("SkinMeta.IsLight") && (bool)dict["SkinMeta.IsLight"];
                    _skins.Add(new SkinInfo(id, name, isLight));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "皮肤文件损坏，已跳过：{File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "扫描皮肤目录失败：{Dir}", ThemesDir);
        }
        Log.Information("皮肤清单（{Count} 套）：{Ids}", _skins.Count, string.Join(", ", _skins.Select(s => s.Id)));
    }

    /// <summary>从磁盘加载皮肤字典（loose XAML，纯画刷无交叉引用，可安全外置）。解析异常向上抛。</summary>
    public static ResourceDictionary? LoadSkinDictionary(string id)
    {
        var path = Path.Combine(ThemesDir, id + ".xaml");
        if (!File.Exists(path)) return null;
        var dict = new ResourceDictionary { Source = new Uri(Path.GetFullPath(path), UriKind.Absolute) };
        _ = dict.Count; // 触发解析：坏文件在此抛出，调用方决定回退
        return dict;
    }

    // ---------- 热重载 ----------

    private static void WatchThemes()
    {
        try
        {
            if (!Directory.Exists(ThemesDir)) return;
            _watcher = new FileSystemWatcher(ThemesDir)
            {
                Filter = "*.xaml",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            _watcher.Changed += OnSkinFileTouched;
            _watcher.Created += OnSkinFileTouched;
            _watcher.Renamed += OnSkinFileTouched;
            _watcher.EnableRaisingEvents = true;
            Log.Debug("皮肤目录监听已开启：{Dir}", ThemesDir);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "皮肤目录监听失败（热重载不可用）");
        }
    }

    private static void OnSkinFileTouched(object sender, FileSystemEventArgs e)
    {
        try
        {
            var id = Path.GetFileNameWithoutExtension(e.Name ?? "");
            if (id != ActiveSkin) return; // 只热重载当前皮肤；其他文件的变更等下次扫描/启用
        }
        catch { return; }
        // 文件池线程回调 → 转到 UI 线程防抖合并（编辑器保存常触发多次事件）
        Application.Current?.Dispatcher.BeginInvoke(QueueSkinReload);
    }

    private static void QueueSkinReload()
    {
        if (Application.Current is null) return;
        _pendingSkinReload = ActiveSkin;
        if (_reloadTimer is null)
        {
            _reloadTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            _reloadTimer.Tick += (_, _) =>
            {
                _reloadTimer.Stop();
                var id = _pendingSkinReload;
                _pendingSkinReload = null;
                if (id is null || id != ActiveSkin) return;
                try
                {
                    Apply(); // Apply 先完整解析新字典，成功才替换（原子换层）
                    Log.Information("皮肤热重载完成：{Id}", id);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "皮肤热重载失败，保持原样：{Id}", id);
                    ToastService.Show("皮肤文件有问题，已保持原样");
                }
            };
        }
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    // ---------- 应用 ----------

    private static void Apply()
    {
        // 生效皮肤损坏/缺失时，按其深浅组回落内置 Dark/Light（仍按皮肤模型换层，机制一致）
        ResourceDictionary layer;
        bool light;
        var info = _skins.FirstOrDefault(s => s.Id == ActiveSkin);
        light = info?.IsLight ?? false;

        try
        {
            var skin = LoadSkinDictionary(ActiveSkin); // 抛异常 = 上层决定（热重载保持原样；启动回落）
            if (skin is null)
            {
                Log.Warning("皮肤文件不存在，回落内置主题：{Id}", ActiveSkin);
                layer = WrapPlain(light);
            }
            else
            {
                // 两层合并：按深浅用内置主题打底（皮肤缺键兜底），皮肤覆盖
                layer = new ResourceDictionary();
                layer.MergedDictionaries.Add(new ResourceDictionary { Source = light ? LightUri : DarkUri });
                layer.MergedDictionaries.Add(skin);
                layer[ThemeLayerKey] = true;
                if (skin.Contains("SkinMeta.IsLight")) light = (bool)skin["SkinMeta.IsLight"];
            }
        }
        catch (Exception) when (Application.Current?.Resources.MergedDictionaries.Any(d => d.Contains(ThemeLayerKey)) == true)
        {
            // 已有主题层在场（运行期）：保持原样，异常交给调用方记录
            throw;
        }
        catch (Exception ex)
        {
            // 启动期首装失败：回落内置主题
            Log.Error(ex, "皮肤加载失败（启动），回落内置主题：{Id}", ActiveSkin);
            layer = WrapPlain(light);
        }

        var dicts = Application.Current.Resources.MergedDictionaries;
        for (var i = dicts.Count - 1; i >= 0; i--)
        {
            var d = dicts[i];
            var path = d.Source?.OriginalString;
            if (d.Contains(ThemeLayerKey)
                || (path?.Contains("Dark.xaml") ?? false)
                || (path?.Contains("Light.xaml") ?? false))
                dicts.RemoveAt(i);
        }
        dicts.Add(layer); // 后加者优先

        foreach (Window window in Application.Current.Windows)
            ApplyCaptionTheme(window, light);
        GlassWindow.RefreshAll();
    }

    private static ResourceDictionary WrapPlain(bool light)
    {
        var layer = new ResourceDictionary { Source = light ? LightUri : DarkUri };
        layer[ThemeLayerKey] = true;
        return layer;
    }

    private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() =>
        {
            if (!FollowSystem) return;
            var target = SystemPrefersLight() ? LightSkin : DarkSkin;
            if (target is not { Length: > 0 } || target == ActiveSkin) return;
            ActiveSkin = target;
            try { Persist(); } catch (Exception error) { Log.Error(error, "系统主题偏好未保存"); }
            Apply();
        });
    }
    public static void Shutdown()
    {
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        _reloadTimer?.Stop(); _watcher?.Dispose(); _watcher = null;
    }

    /// <summary>窗口句柄就绪后调用，让系统标题栏随主题变黑/白（皮肤按自身声明）。</summary>
    public static void ApplyCaptionTheme(Window window) => ApplyCaptionTheme(window, IsLightEffective);

    private static void ApplyCaptionTheme(Window window, bool light)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = light ? 0 : 1;
        var hr = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        if (hr != 0)
            hr = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
        if (hr != 0)
        {
            Log.Debug("标题栏深浅设置失败：hwnd=0x{Hwnd}，hr=0x{Hr:X8}", hwnd.ToInt64(), hr);
            return;
        }
        // 部分 Win10 build 收到新属性后不主动重绘标题栏，强制刷一次非客户区
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNosize | SwpNomove | SwpNozorder | SwpFramechanged);
    }
}
