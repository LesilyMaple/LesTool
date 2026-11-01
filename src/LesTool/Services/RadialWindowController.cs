using System.Windows;
using System.Windows.Threading;
using LesTool.Views;
using Serilog;

namespace LesTool.Services;

/// <summary>
/// 轮盘窗口控制器：启动时预创建隐藏窗口（HWND 与渲染路径已就绪），
/// 触发时只做定位 + Show，把首次显示的开销全部移到启动期。
/// 自愈：窗口被外部关闭（Alt+F4 / 反作弊清理覆盖层等）不再永久罢工——
/// 触发时发现窗口不在或显示失败即重建，并留日志记录被关时间。
/// </summary>
public sealed class RadialWindowController
{
    private RadialWindow? _window;
    private Dispatcher? _dispatcher;

    /// <summary>须在 UI 线程调用。</summary>
    public void Initialize()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        CreateWindow();
    }

    /// <summary>线程安全：钩子线程可直接调用，内部封送到 UI 线程。</summary>
    public void ShowAt(Point centerScreenPx)
    {
        var dispatcher = _dispatcher ??= Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(() =>
        {
            var window = _window ?? CreateWindow();
            try
            {
                window.ShowAt(centerScreenPx);
                Log.Debug("轮盘已显示");
            }
            catch (Exception ex)
            {
                // 窗口处于已关闭状态等：重建一次并重试，避免整段会话轮盘永久失效
                Log.Error(ex, "轮盘显示失败，重建窗口后重试");
                window = CreateWindow();
                window.ShowAt(centerScreenPx);
                Log.Information("轮盘窗口已重建并显示");
            }
        });
    }

    /// <summary>线程安全。</summary>
    public void Hide()
    {
        var dispatcher = _dispatcher ??= Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(() =>
        {
            try
            {
                _window?.Hide();
                Log.Debug("轮盘已隐藏");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "轮盘隐藏失败，丢弃旧窗口待重建");
                _window = null;
            }
        });
    }

    /// <summary>轮盘当前是否可见（快捷键呼出做开关用；UI 线程查询）。</summary>
    public bool IsVisible => _window?.IsVisible ?? false;

    /// <summary>在 UI 线程创建并预热窗口；窗口被外部关闭时置空、下次触发重建。</summary>
    private RadialWindow CreateWindow()
    {
        var window = new RadialWindow();
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
                Log.Information("轮盘窗口被外部关闭（将在下次触发时自动重建）");
            }
        };
        window.PrepareForFastShow();
        _window = window;
        return window;
    }
}
