using System.IO;
using System.Text.Json;
using Serilog;

namespace LesTool.Services;

public class AppSettingsData
{
    /// <summary>ThemeMode 的字符串形式（Dark/Light/System），解析失败回退 Dark。</summary>
    public string Theme { get; set; } = nameof(ThemeMode.Dark);

    public List<HotkeyBinding> Hotkeys { get; set; } = new();

    /// <summary>托盘常驻配置（图标/名称/弹出面板）。旧 settings.json 无此节时取默认值。</summary>
    public TraySettings Tray { get; set; } = new();
}

public class TraySettings
{
    /// <summary>托盘图标：speaker（喇叭轮廓）/ speaker3（波纹实心）/ app（应用图标）/ file（自定义文件）。</summary>
    public string Icon { get; set; } = "speaker";

    /// <summary>Icon == file 时生效，图标文件路径（.ico/.png）。</summary>
    public string? IconFile { get; set; }

    /// <summary>托盘悬停名称（ToolTip），左键行为提示拼在后半句。</summary>
    public string Name { get; set; } = "LesTool · 音量混音器";

    /// <summary>左键托盘执行的命令 Id（ActionRegistry，命令清单见托盘页）。
    /// 旧字段 Panel（写死 volume）已废弃：旧 settings.json 里的值加载时被忽略。</summary>
    public string LeftClickAction { get; set; } = "volume.mixer";
}

public static class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LesTool", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettingsData Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var raw = JsonSerializer.Deserialize<AppSettingsData>(File.ReadAllText(SettingsPath));
                if (raw != null)
                {
                    Log.Debug("设置已加载：{Path}，Theme={Theme}", SettingsPath, raw.Theme);
                    return raw;
                }
            }
            else
            {
                Log.Debug("设置文件不存在，使用默认值：{Path}", SettingsPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取设置失败，使用默认值");
        }
        return new AppSettingsData();
    }

    public static void Save(AppSettingsData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data, JsonOptions));
            Log.Debug("设置已保存：{Path}，Theme={Theme}", SettingsPath, data.Theme);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存设置失败");
        }
    }

    public static ThemeMode ParseTheme(string value) =>
        Enum.TryParse<ThemeMode>(value, ignoreCase: true, out var mode) ? mode : ThemeMode.Dark;
}
