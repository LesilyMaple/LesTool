using System.Windows;
using LesTool.Views;

namespace LesTool.Services;

/// <summary>
/// 轮盘窗口控制器：启动时预创建隐藏窗口（HWND 与渲染路径都已就绪），
/// 手势触发时只做定位 + Show，把所有开销挪到启动期。
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
        window.Dispatcher.BeginInvoke(() => window.ShowAt(centerScreenPx));
    }
}
