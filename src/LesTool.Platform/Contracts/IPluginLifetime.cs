namespace LesTool.Platform;

public interface IPluginLifetimeHost { IPluginLifetime Lifetime { get; } }
public interface IPluginLifetime
{
    bool IsActive { get; }
    CancellationToken Stopping { get; }
    T Own<T>(T resource) where T : IDisposable;
    void OnStopping(Action stop);
    void OnStarted(Action start);
}

public static class PluginLifetimeExtensions
{
    public static void OwnCleanup(this ILesHost host, Action cleanup)
    {
        if (host is IPluginLifetimeHost owner) owner.Lifetime.Own(new Cleanup(cleanup));
    }
    public static bool CanDispatch(this ILesHost host) => host is not IPluginLifetimeHost owner || owner.Lifetime.IsActive;
    private sealed class Cleanup(Action action) : IDisposable
    {
        private Action? _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
