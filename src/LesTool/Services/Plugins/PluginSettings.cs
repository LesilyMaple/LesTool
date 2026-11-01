using System.IO;
using System.Text.Json;
using LesTool.Platform;
using Serilog;

namespace LesTool.Services.Plugins;

/// <summary>
/// 插件独立设置实现：JSON 键值表，落 %APPDATA%\LesTool\plugins\&lt;id&gt;\settings.json。
/// 懒加载；读失败回退默认值；写失败只记警告（与宿主 AppSettings 同一容错级别）。
/// </summary>
internal sealed class PluginSettings : IPluginSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private Dictionary<string, JsonElement> _values = new();
    private bool _loaded;

    public PluginSettings(string pluginId)
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LesTool", "plugins", pluginId, "settings.json");
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(_path))
                _values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(_path)) ?? new Dictionary<string, JsonElement>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "插件设置读取失败，全部用默认值：{Path}", _path);
        }
    }

    public T Get<T>(string key, T fallback)
    {
        EnsureLoaded();
        if (!_values.TryGetValue(key, out var element)) return fallback;
        try
        {
            return JsonSerializer.Deserialize<T>(element) ?? fallback;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "插件设置项解析失败，用默认值：{Key}", key);
            return fallback;
        }
    }

    public void Set<T>(string key, T value)
    {
        EnsureLoaded();
        _values[key] = JsonSerializer.SerializeToElement(value);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_values, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "插件设置保存失败：{Path}", _path);
        }
    }
}
