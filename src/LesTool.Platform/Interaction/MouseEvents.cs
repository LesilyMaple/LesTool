using System.Windows;

namespace LesTool.Platform;

/// <summary>
/// 平台鼠标事件枢纽：低级钩子观察到的事件经这里转发给各功能模块。
/// 订阅方拿到的回调一律在 UI 线程（封送由枢纽统一完成），
/// 免除各订阅方自行 Dispatcher 封送——插件将来订阅同一事件，纪律一致。
/// 枢纽本身不在钩子线程做任何订阅方代码调用，钩子回调只投递。
/// </summary>
public static class MouseEvents
{
    /// <summary>滚轮观察（UI 线程回调）。参数：光标物理坐标、滚轮增量（±120 的倍数）。无订阅者时零开销。</summary>
    public static event Action<Point, int>? Wheel;

    /// <summary>钩子线程调用：封送到 UI 线程后广播。宿主在应用退出后（Application.Current 为空）静默丢弃。</summary>
    public static void RaiseWheel(Point cursorPhys, int delta)
    {
        var app = Application.Current;
        if (app is null) return;
        if (app.Dispatcher.CheckAccess())
            Wheel?.Invoke(cursorPhys, delta);
        else
            app.Dispatcher.BeginInvoke(() => Wheel?.Invoke(cursorPhys, delta));
    }
}
