using System.Runtime.InteropServices;
using System.Windows;
using LesTool.Platform;

namespace LesTool.Services.Scripting;

/// <summary>
/// 脚本的高频便捷入口。糖，不是权限闸——脚本已引用应用全部已加载程序集，
/// 任何 public 类型（含整个 LesTool 命名空间）都能直接用；这里只放少打字的门面。
/// 线程无关：脚本跑在后台线程，剪贴板等 UI 线程依赖在此统一封送。
/// 进程启动不在门面里：脚本直接 System.Diagnostics.Process.Start 即可，能力无差别。
/// 以 __Run(LesApi Les) 参数形式注入脚本作用域，脚本里写 Les.Xxx。
/// </summary>
public sealed class LesApi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    /// <summary>鼠标物理像素坐标（与全应用口径一致），任意线程可调。</summary>
    public System.Windows.Point Mouse
    {
        get
        {
            GetCursorPos(out var p);
            return new System.Windows.Point(p.X, p.Y);
        }
    }

    /// <summary>读写剪贴板文本；写 null 等同写空串。自家写入统一打自发标记。</summary>
    public string? ClipboardText
    {
        get => OnUi(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        set => OnUi(() =>
        {
            ClipboardService.MarkSelfWrite();
            Clipboard.SetText(value ?? "");
        });
    }

    /// <summary>右下角浮出提示，自动消失。</summary>
    public void Toast(string message) => ToastService.Show(message);

    /// <summary>宿主应用实例——从这里够到整个应用的运行期状态（窗口、设置、各服务）。</summary>
    public App App => (App)Application.Current;

    /// <summary>全局共享设置实例。</summary>
    public static AppSettingsData Settings => App.Settings;

    private static void OnUi(Action action)
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.Invoke(action);
    }

    private static T OnUi<T>(Func<T> func)
    {
        var app = Application.Current;
        return app is null || app.Dispatcher.CheckAccess() ? func() : app.Dispatcher.Invoke(func);
    }
}
