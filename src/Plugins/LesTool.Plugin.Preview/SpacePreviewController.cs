using System.Windows.Threading;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.Preview;

internal sealed class SpacePreviewController
{
    private const uint Space = 0x20, Escape = 0x1B;
    private readonly IPluginSettings _settings;
    private readonly Action<string> _toast;
    private readonly Func<bool> _canDispatch;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly CancellationTokenSource _stopping = new();
    private volatile bool _isOpen, _pending, _spaceOwned, _escapeOwned;
    private volatile bool _enabled;
    private bool _follow, _polling;
    private int _generation;
    private WindowTarget _target;
    private PreviewWindow? _window;
    private IReadOnlyList<string> _paths = Array.Empty<string>();

    public SpacePreviewController(IPluginSettings settings, Action<string> toast, Func<bool>? canDispatch = null)
    {
        _settings = settings; _toast = toast; _canDispatch = canDispatch ?? (() => true);
        _poll.Tick += (_, _) => _ = PollAsync();
    }
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _settings.Set("enabled", value);
            ApplyEnabled(value);
        }
    }
    private void ApplyEnabled(bool value)
    {
        if (_enabled == value) return;
        _enabled = value;
        if (value) KeyboardEvents.FilterKey += OnFilterKey;
        else { KeyboardEvents.FilterKey -= OnFilterKey; Close("disabled"); }
    }
    public bool FollowSelection { get => _follow; set { _settings.Set("follow", value); _follow = value; } }
    public void Initialize() { _follow = _settings.Get("follow", true); ApplyEnabled(_settings.Get("enabled", true)); }
    public void Shutdown()
    {
        if (_stopping.IsCancellationRequested) return;
        _stopping.Cancel();
        KeyboardEvents.FilterKey -= OnFilterKey;
        Close("shutdown"); _window?.Close(); _window = null;
    }
    private bool OnFilterKey(KeyHookMessage message, uint key)
    {
        if (!_canDispatch() || !_enabled || _stopping.IsCancellationRequested || (key != Space && key != Escape)) return false;
        var down = message is KeyHookMessage.KeyDown or KeyHookMessage.SysKeyDown;
        if (!down)
        {
            if (key == Space) { var owned = _spaceOwned; _spaceOwned = false; return owned; }
            var esc = _escapeOwned; _escapeOwned = false; return esc;
        }
        if (!ExplorerSelection.IsForegroundExplorer()) return false;
        if (key == Escape)
        {
            if (!_isOpen && !_pending) return false;
            _escapeOwned = true; BeginInvoke(() => Close("escape")); return true;
        }
        if (_spaceOwned) return true;
        _spaceOwned = true;
        if (_pending) return true;
        if (_isOpen) BeginInvoke(() => Close("space"));
        else
        {
            _pending = true;
            var target = WindowTarget.Capture();
            var generation = Interlocked.Increment(ref _generation);
            BeginInvoke(() => _ = OpenAsync(target, generation));
        }
        return true;
    }
    private async Task OpenAsync(WindowTarget target, int generation)
    {
        try
        {
            if (generation != _generation || !_enabled || !_canDispatch() || _stopping.IsCancellationRequested) return;
            if (await ExplorerSelection.IsFocusInEditAsync(target, _stopping.Token))
            { if (generation == _generation && _enabled && _canDispatch() && target.IsForeground) KeyboardEvents.ReplayKey(Space); return; }
            var selection = await ExplorerSelection.GetSelectionAsync(target, _stopping.Token);
            if (generation != _generation || !_enabled || !target.IsForeground || !_canDispatch() || _stopping.IsCancellationRequested) return;
            if (selection is null || selection.Paths.Count == 0) { KeyboardEvents.ReplayKey(Space); return; }
            _target = target; _paths = selection.Paths;
            if (_window is null)
            {
                _window = new PreviewWindow();
                _window.Closed += (_, _) => { _isOpen = false; _window = null; Interlocked.Increment(ref _generation); _poll.Stop(); };
            }
            _window.Present(selection); _isOpen = true; _poll.Start();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log.Error(error, "预览打开失败"); _toast("文件预览失败，详见日志"); }
        finally { if (generation == _generation) _pending = false; }
    }
    private async Task PollAsync()
    {
        if (_polling || !_isOpen) return;
        if (!_target.IsForeground || !_canDispatch()) { Close("target changed"); return; }
        if (!_follow) return;
        _polling = true; var generation = _generation;
        try
        {
            var selection = await ExplorerSelection.GetSelectionAsync(_target, _stopping.Token);
            if (generation != _generation || !_isOpen || !_target.IsForeground || _stopping.IsCancellationRequested) return;
            // A timed-out provider is not evidence that the user's selection was cleared.
            if (selection is null || selection.Paths.Count == 0) return;
            if (_paths.SequenceEqual(selection.Paths, StringComparer.OrdinalIgnoreCase)) return;
            _paths = selection.Paths; _window?.Present(selection);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log.Debug(error, "预览跟随查询失败"); }
        finally { _polling = false; }
    }
    private void Close(string reason)
    {
        _generation++; _pending = false; _poll.Stop();
        var open = _isOpen; _isOpen = false;
        if (open) _window?.Dismiss();
        Log.Debug("预览会话关闭：{Reason}", reason);
    }
    private static void BeginInvoke(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action);
    }
}
