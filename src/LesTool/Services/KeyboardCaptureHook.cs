using System.Runtime.InteropServices;
using System.Windows.Input;
using Serilog;

namespace LesTool.Services;

/// <summary>
/// 捕获期间的临时 WH_KEYBOARD_LL 键盘钩子。
/// 已被 RegisterHotKey 注册的组合（如 QQ 截图 Ctrl+Alt+A、锁定QQ Ctrl+Alt+L）
/// 会被系统整组吞掉直接派给注册方，WPF 键盘事件根本收不到——低级钩子先于
/// 热键分发执行，这里全量拦截：按键只喂捕获状态机，注册方与其他程序均不触发。
/// 专属线程跑消息泵（同 MouseHookService），捕获开始 Install、结束 Uninstall，
/// 存活期极短；若安装失败则退回 WPF 键盘事件路径（收不到被占组合但不影响其余功能）。
/// </summary>
internal static class KeyboardCaptureHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_QUIT = 0x0012;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
    // 查询用通用码（左右都算），跟踪用左右区分码
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    /// <summary>拦截到的按键与当时的修饰键（钩子线程回调；订阅方必须异步封送 UI 线程，不得同步 Invoke，否则卸载时死锁）。</summary>
    internal static event Action<Key, ModifierKeys>? KeyIntercepted;

    /// <summary>修饰键按住状态变化（钩子线程回调；捕获页用于实时预览，同样须异步封送）。</summary>
    internal static event Action<ModifierKeys>? ModifiersChanged;

    private static readonly object Gate = new();
    private static HookProc? _hookProc; // 字段持引用：委托被 GC 后回调悬空会导致进程崩溃
    private static Thread? _thread;
    private static uint _threadId;
    private static IntPtr _hookHandle;
    private static volatile bool _stopping; // 线程启动与卸载并发时，装完钩子后自查退出

    /// <summary>
    /// 修饰键状态自跟踪。被钩子吞掉的按键不会进系统键状态表，
    /// GetAsyncKeyState 查不到我们拦下的 Ctrl/Alt——必须在回调里自己记按下/抬起。
    /// </summary>
    private static int _modifierState;

    internal static void Install()
    {
        lock (Gate)
        {
            if (_thread is not null) return;
            _stopping = false;
            _thread = new Thread(RunHookLoop) { IsBackground = true, Name = "LesTool.KeyboardCaptureHook" };
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
            Log.Error("键盘捕获钩子安装失败，错误码 {ErrorCode}，退回 WPF 键盘事件路径", Marshal.GetLastWin32Error());
            return;
        }
        Log.Debug("键盘捕获钩子已安装（线程 {ThreadId}）", _threadId);
        // 安装前已按住的修饰键从系统状态表取初值（此后事件被吞，表不再更新）
        _modifierState = 0;
        if (IsPressed(VK_CONTROL)) _modifierState |= (int)MOD_CONTROL;
        if (IsPressed(VK_MENU)) _modifierState |= (int)MOD_ALT;
        if (IsPressed(VK_SHIFT)) _modifierState |= (int)MOD_SHIFT;
        if (IsPressed(VK_LWIN) || IsPressed(VK_RWIN)) _modifierState |= (int)MOD_WIN;
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
            Log.Debug("键盘捕获钩子已卸载");
        }
    }

    private static IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = (long)wParam;
            if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
            {
                try
                {
                    var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    var modifierBit = ModifierBitOf(data.vkCode);
                    if (modifierBit != 0)
                    {
                        // 纯修饰键：只更新跟踪状态并广播预览，不作为主键上报
                        var before = _modifierState;
                        _modifierState |= modifierBit;
                        if (_modifierState != before)
                            ModifiersChanged?.Invoke(ToModifierKeys(_modifierState));
                    }
                    else
                    {
                        var key = KeyInterop.KeyFromVirtualKey((int)data.vkCode);
                        KeyIntercepted?.Invoke(key, ToModifierKeys(_modifierState));
                    }
                }
                catch
                {
                    // 绝不让异常逃逸进本机回调
                }
                return new IntPtr(1); // 拦截：热键注册方收不到，避免捕获时误触其他程序
            }

            if (message == WM_KEYUP || message == WM_SYSKEYUP)
            {
                try
                {
                    var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    var modifierBit = ModifierBitOf(data.vkCode);
                    if (modifierBit != 0)
                    {
                        var before = _modifierState;
                        _modifierState &= ~modifierBit;
                        if (_modifierState != before)
                            ModifiersChanged?.Invoke(ToModifierKeys(_modifierState));
                    }
                }
                catch
                {
                    // 绝不让异常逃逸进本机回调
                }
                return new IntPtr(1); // 抬键一并吞掉，不给系统留下按下未抬的状态
            }
        }
        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private static int ModifierBitOf(uint vk) => vk switch
    {
        VK_LCONTROL or VK_RCONTROL => (int)MOD_CONTROL,
        VK_LMENU or VK_RMENU => (int)MOD_ALT,
        VK_LSHIFT or VK_RSHIFT => (int)MOD_SHIFT,
        VK_LWIN or VK_RWIN => (int)MOD_WIN,
        _ => 0
    };

    private static ModifierKeys ToModifierKeys(int state)
    {
        var modifiers = ModifierKeys.None;
        if ((state & MOD_CONTROL) != 0) modifiers |= ModifierKeys.Control;
        if ((state & MOD_ALT) != 0) modifiers |= ModifierKeys.Alt;
        if ((state & MOD_SHIFT) != 0) modifiers |= ModifierKeys.Shift;
        if ((state & MOD_WIN) != 0) modifiers |= ModifierKeys.Windows;
        return modifiers;
    }

    private static bool IsPressed(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    static KeyboardCaptureHook() => _hookProc = HookCallback;

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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    #endregion
}
