using System.Runtime.InteropServices;
using Serilog;

namespace LesTool.Services;

/// <summary>
/// 轮盘显示期间的临时 WH_KEYBOARD_LL 键盘钩子：只观察并吞掉 Esc（取消轮盘），
/// 其余按键一律 CallNextHookEx 原样放行——与捕获钩子（全量拦截）的风险等级刻意不同：
/// 轮盘窗口期极短（右键按住期间），但若钩子线程卡顿，放行式钩子最多丢失一次 Esc
/// 取消，不会影响打字。轮盘每次显示 Install、收起 Uninstall；安装失败只是 Esc
/// 取消不可用（松开右键仍可收起），不影响其他功能。
/// </summary>
internal static class RadialEscapeHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_QUIT = 0x0012;
    private const uint VK_ESCAPE = 0x1B;
    private const uint LLMHF_INJECTED = 0x01;
    private const uint LLMHF_LOWERIL_INJECTED = 0x02;

    /// <summary>Esc 按下（钩子线程回调；订阅方必须异步封送 UI 线程，不得同步 Invoke，否则卸载时死锁）。</summary>
    internal static event Action? EscPressed;

    private static readonly object Gate = new();
    private static HookProc? _hookProc; // 字段持引用：委托被 GC 后回调悬空会导致进程崩溃
    private static Thread? _thread;
    private static uint _threadId;
    private static IntPtr _hookHandle;
    private static volatile bool _stopping; // 线程启动与卸载并发时，装完钩子后自查退出

    static RadialEscapeHook() => _hookProc = HookCallback;

    internal static void Install()
    {
        lock (Gate)
        {
            if (_thread is not null) return;
            _stopping = false;
            _thread = new Thread(RunHookLoop) { IsBackground = true, Name = "LesTool.RadialEscapeHook" };
            _thread.Start();
        }
    }

    internal static void Uninstall()
    {
        Thread? thread;
        lock (Gate)
        {
            thread = _thread;
            _thread = null;
        }
        if (thread is null) return;
        _stopping = true;
        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(2000);
    }

    private static void RunHookLoop()
    {
        _threadId = GetCurrentThreadId();
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc!, GetModuleHandle(null), 0);
        if (_hookHandle == IntPtr.Zero)
        {
            Log.Error("轮盘 Esc 钩子安装失败，错误码 {ErrorCode}（本次显示仅少一种取消方式）", Marshal.GetLastWin32Error());
            return;
        }
        Log.Debug("轮盘 Esc 钩子已安装（线程 {ThreadId}）", _threadId);
        if (_stopping)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            return;
        }

        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            Log.Debug("轮盘 Esc 钩子已卸载");
        }
    }

    private static IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && ((long)wParam == WM_KEYDOWN || (long)wParam == WM_SYSKEYDOWN))
        {
            try
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool injected = (data.flags & (LLMHF_INJECTED | LLMHF_LOWERIL_INJECTED)) != 0;
                if (!injected && data.vkCode == VK_ESCAPE)
                {
                    EscPressed?.Invoke();
                    return new IntPtr(1); // 吞掉：Esc 只用来取消轮盘，不再传给前台程序
                }
            }
            catch
            {
                // 绝不让异常逃逸进本机回调
            }
        }
        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    #region P/Invoke

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    #endregion
}
