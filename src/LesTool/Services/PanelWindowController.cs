using System.Windows;
using LesTool.Platform;
using LesTool.Views;
using Serilog;

namespace LesTool.Services;

/// <summary>
/// 命令面板控制器：启动预创建壳窗口（HWND 预热，触发只做定位 + Show）。
/// 与轮盘互不干涉（2026-10-13 拍板）：轮盘靠右键按住拖动触发，面板打字期间
/// 该手势不会发生，无需代码互斥。
/// </summary>
public sealed class PanelWindowController
{
    private PanelShellWindow? _window;

    /// <summary>须在 UI 线程调用。</summary>
    public void Initialize()
    {
        if (_window is not null) return;
        _window = new PanelShellWindow();
        _window.Hidden += () => Log.Information("面板已收起");
        _window.PrepareForFastShow();
    }

    /// <summary>呼出/收起开关（命令入口）。线程安全。</summary>
    public void Toggle()
    {
        var window = _window;
        if (window is null) return;
        window.Dispatcher.BeginInvoke(() =>
        {
            if (window.IsVisible && !window.IsClosing) Hide();
            else ShowAtCursor();
        });
    }

    /// <summary>在当前光标所在显示器呼出。线程安全。</summary>
    public void ShowAtCursor()
    {
        var window = _window;
        if (window is null) return;
        var cursor = GdiCapture.GetCursorPosition(); // 任意线程可取
        window.Dispatcher.BeginInvoke(() =>
        {
            window.ShowAt(cursor);
            Log.Information("面板已呼出（锚点 {Anchor}）", cursor);
        });
    }

    /// <summary>收起。线程安全。</summary>
    public void Hide()
    {
        var window = _window;
        if (window is null) return;
        window.Dispatcher.BeginInvoke(() => window.HideAnimated());
    }

    /// <summary>面板当前是否可见（UI 线程查询）。</summary>
    public bool IsVisible => _window?.IsVisible ?? false;
}
