using System.Windows;
using LesTool.Views;
using Serilog;

namespace LesTool.Services;

/// <summary>
/// 轮盘窗口控制器：启动时预创建隐藏窗口（HWND 与渲染路径已就绪），
/// 触发时只做定位 + Show，把首次显示的开销全部移到启动期。
/// </summary>
public sealed class RadialWindowController
{
    private RadialWindow? _window;

    /// <summary>须在 UI 线程调用。</summary>
    public void Initialize()
    {
        if (_window is not null) return;
        _window = new RadialWindow();
        _window.PrepareForFastShow();
    }

    /// <summary>线程安全：钩子线程可直接调用，内部封送到 UI 线程。</summary>
    public void ShowAt(Point centerScreenPx)
    {
        var window = _window;
        if (window is null) return;
        window.Dispatcher.BeginInvoke(() =>
        {
            window.ShowAt(centerScreenPx);
            Log.Debug("轮盘已显示");
        });
    }

    /// <summary>线程安全。</summary>
    public void Hide()
    {
        var window = _window;
        if (window is null) return;
        window.Dispatcher.BeginInvoke(() =>
        {
            window.Hide();
            Log.Debug("轮盘已隐藏");
        });
    }
}
