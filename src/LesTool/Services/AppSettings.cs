using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using LesTool.Platform.Storage;

namespace LesTool.Services;

public class AppSettingsData
{
    public int SchemaVersion { get; set; } = 1;
    public bool? LauncherHotkeyInitialized { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    /// <summary>生效皮肤 id（如 glass/aurora/fluent/paper）。旧值 Dark/Light/System/Skin:x 由 ThemeManager 迁移。</summary>
    public string Theme { get; set; } = "glass";

    /// <summary>是否跟随系统深浅主题：开启时按系统在深/浅两组所选皮肤间自动切换。</summary>
    public bool ThemeFollowSystem { get; set; }

    /// <summary>深色组所选皮肤 id。</summary>
    public string ThemeDark { get; set; } = "glass";

    /// <summary>浅色组所选皮肤 id。</summary>
    public string ThemeLight { get; set; } = "fluent";

    /// <summary>日志自动清理周期（LogRetentionMode 的字符串形式：Daily/Weekly/Monthly），
    /// 解析失败回退 Weekly（与历史硬编码的保留 7 天一致）。</summary>
    public string LogRetention { get; set; } = nameof(LogRetentionMode.Weekly);

    public List<HotkeyBinding> Hotkeys { get; set; } = new();

    /// <summary>轮盘（2026-10-16 场景化：全局默认 + 按应用覆盖，对齐 Quicker）。</summary>
    public RadialSettingsData Radial { get; set; } = new();

    /// <summary>主窗口尺寸记忆（2026-10-17：关闭时保存、启动还原；null=首次启动用默认）。</summary>
    public MainWindowSettings? MainWindow { get; set; }
}

public class MainWindowSettings
{
    /// <summary>窗口宽度（DIP）。</summary>
    public double Width { get; set; }

    /// <summary>窗口高度（DIP）。</summary>
    public double Height { get; set; }
}

public class RadialSettingsData
{
    /// <summary>轮盘总槽位：内圈 8 + 外圈 8 + 扩展圈 8（对齐 Quicker 的 24 格）。</summary>
    public const int SlotCount = 24;

    /// <summary>每圈的方位数。</summary>
    public const int RingSlots = 8;

    /// <summary>内圈槽位起始下标（0-7）。</summary>
    public const int InnerFirst = 0;

    /// <summary>外圈槽位起始下标（8-15）。</summary>
    public const int OuterFirst = 8;

    /// <summary>扩展圈槽位起始下标（16-23）：盘外不可见区域，按方向命中，仅全局场景生效。</summary>
    public const int ExtensionFirst = 16;

    /// <summary>可选轮盘直径（DIP）：小 420 / 中 480 / 大 570（2026-10-17 用户拍板整体放大 1.5 倍）。</summary>
    public static readonly int[] AllowedSizes = { 420, 480, 570 };

    /// <summary>场景列表；[0] 恒为全局场景（Process 为 null），其余为按应用覆盖场景。</summary>
    public List<RadialSceneSettings> Scenes { get; set; } = new();

    /// <summary>轮盘显示后未选择自动收起的秒数。</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>轮盘直径（DIP）。</summary>
    public int SizeDip { get; set; } = 480;

    /// <summary>旧版扁平槽位（场景化之前）；Load 时迁入全局场景，保存时不再写出。</summary>
    public List<RadialSlotSettings>? Slots { get; set; }

    [JsonIgnore]
    public RadialSceneSettings Global => Scenes[0];

    /// <summary>补齐结构（Load 与编辑后调用）：全局场景在首位、每场景固定 8 槽、数值夹紧。
    /// 旧扁平 Slots 只在无场景时迁移一次。</summary>
    public void Normalize()
    {
        if (Slots is { Count: > 0 } legacy && Scenes.Count == 0)
        {
            Scenes.Add(new RadialSceneSettings { Process = null, Slots = legacy });
            Log.Information("旧版轮盘槽位已迁移为全局场景（{Count} 槽）", legacy.Count);
        }
        Slots = null;

        if (Scenes.Count == 0 || Scenes[0].Process != null)
            Scenes.Insert(0, new RadialSceneSettings());
        NormalizeSceneSlots(Scenes[0]);

        for (var i = 1; i < Scenes.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(Scenes[i].Process))
            {
                Scenes.RemoveAt(i); // 脏数据：空进程名的"应用场景"剔除
                i--;
                continue;
            }
            NormalizeSceneSlots(Scenes[i]);
        }

        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 3, 60);
        if (!AllowedSizes.Contains(SizeDip)) SizeDip = 480;
    }

    private static void NormalizeSceneSlots(RadialSceneSettings scene)
    {
        while (scene.Slots.Count < SlotCount) scene.Slots.Add(new RadialSlotSettings());
        if (scene.Slots.Count > SlotCount) scene.Slots.RemoveRange(SlotCount, scene.Slots.Count - SlotCount);
    }
}

public class RadialSceneSettings
{
    /// <summary>进程名（小写文件名，如 "msedge"）；null = 全局场景（Scenes[0]）。</summary>
    public string? Process { get; set; }

    /// <summary>在此软件下禁用轮盘菜单（手势退化为普通右键单击回放）。</summary>
    public bool Disabled { get; set; }

    public List<RadialSlotSettings> Slots { get; set; } = new();
}

public class RadialSlotSettings
{
    /// <summary>命令 Id（ActionRegistry.Bindable）；null = 空。</summary>
    public string? Action { get; set; }

    /// <summary>应用场景专用：true = 本槽按此处的 Action 生效（null 表示该软件下禁用此槽）；
    /// false = 继承全局同位槽。全局场景忽略此标记。</summary>
    public bool Override { get; set; }
}

public static class AppSettings
{
    private static JsonSettingsStore<AppSettingsData>? _store;
    private static JsonSettingsStore<AppSettingsData> Store => _store ??= new(
        Path.Combine(AppPaths.DataRoot, "settings.json"), () => new AppSettingsData { LauncherHotkeyInitialized = false },
        new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull },
        value =>
        {
            value.Hotkeys ??= new();
            value.Radial ??= new();
            value.Radial.Scenes ??= new();
            if (value.Hotkeys.Any(binding => binding is null || binding.Action is null || binding.Keys is null)
                || value.Radial.Scenes.Any(scene => scene is null || scene.Slots is null || scene.Slots.Any(slot => slot is null))
                || value.Radial.Slots?.Any(slot => slot is null) == true)
                throw new InvalidDataException("设置包含无效的快捷键/轮盘条目");
            value.Theme ??= "glass"; value.ThemeDark ??= "glass"; value.ThemeLight ??= "fluent";
            value.Radial.Normalize();
        });
    public static bool IsNewConfiguration { get; private set; }
    public static bool IsProtected => Store.IsProtected;
    internal static string? ProtectionReason => Store.ReadError;
    internal static void ResetProtected() { var defaults = Store.ResetToDefaults(); defaults.Radial.Normalize(); App.ReplaceSettings(defaults); IsNewConfiguration = false; }

    public static AppSettingsData Load()
    {
        var value = Store.Read();
        value.Radial.Normalize();
        IsNewConfiguration = Store.ReadState == SettingsReadState.NewFile;
        if (value.LauncherHotkeyInitialized is null || Store.IsProtected) value.LauncherHotkeyInitialized = true;
        return value;
    }

    public static void Save(AppSettingsData data) => Store.Save(data);
    public static Task<SaveResult> FlushAsync(CancellationToken token = default) => Store.FlushAsync(token);
}
