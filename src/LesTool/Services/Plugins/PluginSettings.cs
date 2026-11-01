using System.IO;
using System.Text.Json;
using LesTool.Platform;
using LesTool.Platform.Storage;
using Serilog;

namespace LesTool.Services.Plugins;

internal sealed class PluginSettings : IAsyncPluginSettings, IPluginDataDirectory
{
    private readonly JsonSettingsStore<Dictionary<string, JsonElement>> _store;
    private Dictionary<string, JsonElement>? _values;
    private Dictionary<string, JsonElement> Values => _values ??= _store.Read();
    internal bool IsProtected { get { _ = Values; return _store.IsProtected; } }
    public string DataDirectory { get; }
    public PluginSettings(string pluginId)
    {
        DataDirectory = Path.Combine(AppPaths.DataRoot, "plugins", pluginId, "data");
        _store = new(Path.Combine(AppPaths.DataRoot, "plugins", pluginId, "settings.json"), () => new());
    }

    internal void ResetProtected() => _values = _store.ResetToDefaults();

    public T Get<T>(string key, T fallback)
    {
        if (!Values.TryGetValue(key, out var element)) return fallback;
        try { return JsonSerializer.Deserialize<T>(element) ?? fallback; }
        catch (JsonException error) { Log.Warning(error, "插件设置项无效：{Key}", key); return fallback; }
    }

    public void Set<T>(string key, T value)
    {
        Values[key] = JsonSerializer.SerializeToElement(value);
        _store.Save(Values);
    }
    public Task<SaveResult> SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        Values[key] = JsonSerializer.SerializeToElement(value);
        return _store.SaveAsync(Values, cancellationToken);
    }
    public Task<SaveResult> FlushAsync(CancellationToken cancellationToken = default) => _store.FlushAsync(cancellationToken);
}
