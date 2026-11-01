using LesTool.Platform;
using Serilog;

namespace LesTool.Services.Plugins;

internal sealed class PluginRuntime(string id) : IPluginLifetime, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<IDisposable> _resources = new();
    private readonly List<Action> _starts = new(), _stops = new();
    private int _active, _disposed, _quiesced;
    internal string Id { get; } = id;
    internal Guid Epoch { get; } = Guid.NewGuid();
    public bool IsActive => Volatile.Read(ref _active) == 1;
    public CancellationToken Stopping => _stopping.Token;
    public T Own<T>(T resource) where T : IDisposable
    {
        if (_disposed != 0) throw new ObjectDisposedException(nameof(PluginRuntime));
        _resources.Add(resource);
        return resource;
    }
    public void OnStopping(Action stop) => _stops.Add(stop);
    public void OnStarted(Action start) => _starts.Add(start);
    internal void Start() { foreach (var start in _starts) start(); }
    internal void Activate() => Volatile.Write(ref _active, 1);
    internal void Quiesce()
    {
        Volatile.Write(ref _active, 0);
        if (Interlocked.Exchange(ref _quiesced, 1) != 0) return;
        foreach (var stop in _stops.AsEnumerable().Reverse()) Try(stop);
        if (!_stopping.IsCancellationRequested)
        {
            try { _stopping.Cancel(); } catch (Exception e) { Log.Error(e, "插件取消回调失败：{Id}", Id); }
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Quiesce();
        foreach (var resource in _resources.AsEnumerable().Reverse()) Try(resource.Dispose);
        _resources.Clear();
    }
    private void Try(Action action)
    {
        try { action(); } catch (Exception e) { Log.Error(e, "插件资源清理失败：{Id}", Id); }
    }
}
