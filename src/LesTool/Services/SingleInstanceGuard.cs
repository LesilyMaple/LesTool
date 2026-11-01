using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LesTool.Services;

/// <summary>单实例闸：命名互斥体判重，后来者把老实例的主窗口唤到前台后自退。
/// 须在设置加载与日志初始化之前调用——日志文件被首实例独占、热键钩子不能注册两份。
/// 唤起由后来者自己调 SetForegroundWindow：它刚被用户双击拉起、持有前台权限，
/// 跨进程唤窗不受操作系统前台锁限制；唤起尽力而为，失败只影响窗口不弹到前面。</summary>
internal static class SingleInstanceGuard
{
    private const string MutexName = @"Local\LesTool.App.SingleInstance";

    private static Mutex? _mutex;

    /// <summary>首个实例返回 true 并持有互斥体到进程退出；后来者返回 false（已顺手唤起老实例）。</summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew) return true;
        _mutex.Dispose();
        _mutex = null;
        ActivateExisting();
        return false;
    }

    /// <summary>按进程名找老实例的主窗口：最小化则还原，再抢前台。
    /// 主窗口句柄拿不到（全隐藏等边缘态）就静默放弃，反正多出来的实例必退。</summary>
    private static void ActivateExisting()
    {
        try
        {
            int self = Environment.ProcessId;
            foreach (var process in Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName))
            {
                if (process.Id == self) continue;
                IntPtr hwnd = process.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                break;
            }
        }
        catch
        {
            // 唤起失败不影响去重本业
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_RESTORE = 9;
}
