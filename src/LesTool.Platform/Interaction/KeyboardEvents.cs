using System.Runtime.InteropServices;
using Serilog;

namespace LesTool.Platform;

/// <summary>低级键盘钩子观察到的消息类型（KeyDown/SysKeyDown、KeyUp/SysKeyUp）。</summary>
public enum KeyHookMessage
{
    KeyDown,
    KeyUp,
    SysKeyDown,
    SysKeyUp,
}

/// <summary>
/// 平台键盘事件枢纽：常驻 WH_KEYBOARD_LL 条件吞键钩子 + 带标记回放。
/// 订阅驱动安装——首个 FilterKey 订阅者出现时装钩子，退订清零即卸载（零订阅零成本），
/// 与 MouseEvents 枢纽同属宿主/插件共用的公共静态成员。
/// 吞键决策在钩子线程回调内同步完成，因此 FilterKey 必须快（LowLevelHooksTimeout 纪律）：
/// 只许 GetForegroundWindow/GetClassName 级别的本机快调用，禁 IO/COM/UIA/锁等待。
/// 慢判定由订阅方按"先吞后验"完成：回调吞下按键，异步查明条件不成立时 ReplayKey 回放
/// （轮盘 MouseHookService 同款扣住/回放纪律）。回放自带标记，钩子对注入事件一律放行，
/// 不会形成自拦截回环。抑制期（RadialGuard，如截图会话）事件不询问直接放行。
/// </summary>
public static class KeyboardEvents
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_QUIT = 0x0012;

    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLKHF_LOWERIL_INJECTED = 0x20;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const uint VkShift = 0x10;
    private const uint VkControl = 0x11;
    private const uint VkMenu = 0x12; // Alt

    /// <summary>物理修饰键采样表：左右分键，抬起/恢复时对哪个按住操作哪个。</summary>
    private static readonly uint[] PhysicalModifierVks =
    {
        0xA0, 0xA1, // LShift RShift
        0xA2, 0xA3, // LCtrl RCtrl
        0xA4, 0xA5, // LAlt RAlt
        0x5B, 0x5C, // LWin RWin
    };

    /// <summary>回放按键的 dwExtraInfo 标记；钩子据此识别自己注入的事件（与鼠标回放同约定）。</summary>
    private static readonly nuint ReplayMarker = unchecked((nuint)0x4C4553544F4F4C21UL); // "LESTOOL!"

    private static readonly object Gate = new();
    private static Func<KeyHookMessage, uint, bool>? _filter; // 订阅链（委托引用读写原子，回调读快照）
    private static readonly HookProc HookCallbackProc = HookCallback; // 字段持引用：委托被 GC 后回调悬空会导致进程崩溃
    private static Thread? _thread;
    private static uint _threadId;
    private static IntPtr _hookHandle;
    private static volatile bool _stopping; // 线程启动与卸载并发时，装完钩子后自查退出

    /// <summary>
    /// 吞键过滤器（钩子线程回调）：参数为消息与虚拟键码，返回 true 表示吞下该按键。
    /// 多订阅者任一返回 true 即吞。订阅/退订即装卸钩子，须在 UI 线程操作。
    /// </summary>
    public static event Func<KeyHookMessage, uint, bool>? FilterKey
    {
        add
        {
            lock (Gate)
            {
                _filter += value;
                if (_thread is null)
                {
                    _stopping = false;
                    _thread = new Thread(RunHookLoop) { IsBackground = true, Name = "LesTool.KeyboardEvents" };
                    _thread.Start();
                }
            }
        }
        remove
        {
            Thread? thread;
            lock (Gate)
            {
                _filter -= value;
                if (_filter is not null) return;
                thread = _thread;
                _thread = null;
            }
            if (thread is null) return;
            _stopping = true;
            if (_threadId != 0)
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(2000);
        }
    }

    /// <summary>
    /// 把单个按键回放给系统（按下+抬起，带 ReplayMarker 标记）。
    /// 供"先吞后验"的订阅方在判定不成立时把按键还给前台程序。UI 线程调用。
    /// </summary>
    public static void ReplayKey(uint vk)
    {
        var inputs = new[]
        {
            Input(Key(vk, 0)),
            Input(Key(vk, KEYEVENTF_KEYUP)),
        };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            Log.Error("按键回放失败（VK {Vk}），错误码 {ErrorCode}", vk, Marshal.GetLastWin32Error());
        else
            Log.Debug("按键已回放给系统（VK {Vk}）", vk);
    }

    /// <summary>
    /// 向前台窗口发送组合键（如 Ctrl+Shift+T）。注入事件带 ReplayMarker，
    /// 自家钩子一律放行，不会形成自拦截回环。
    /// 发送前先把物理按住的修饰键（Ctrl/Shift/Alt/Win）注入抬起、发完恢复按下：
    /// 快捷键触发时用户手指仍压着热键的 Ctrl/Alt，不先抬起，前台会收到
    /// Ctrl+Alt+T 之类的串味组合导致动作失灵。Home/End 等扩展键须传 extendedKey，
    /// 否则可能被前台当成小键盘键。UI 线程调用。
    /// </summary>
    public static void SendChord(bool ctrl, bool shift, bool alt, uint vk, bool extendedKey = false)
    {
        var sequence = new List<KEYBDINPUT>();

        // 1) 抬起物理按住的修饰键（记下待恢复）；与和弦同名的键也走抬起再按下，状态最干净
        var held = new List<uint>(PhysicalModifierVks.Length);
        foreach (uint modVk in PhysicalModifierVks)
            if ((GetAsyncKeyState((int)modVk) & 0x8000) != 0)
            {
                held.Add(modVk);
                sequence.Add(Key(modVk, KEYEVENTF_KEYUP));
            }

        // 2) 和弦本体：修饰键按下 → 主键按下/抬起 → 修饰键逆序抬起
        uint mainFlags = extendedKey ? KEYEVENTF_EXTENDEDKEY : 0;
        if (ctrl) sequence.Add(Key(VkControl, 0));
        if (shift) sequence.Add(Key(VkShift, 0));
        if (alt) sequence.Add(Key(VkMenu, 0));
        sequence.Add(Key(vk, mainFlags));
        sequence.Add(Key(vk, mainFlags | KEYEVENTF_KEYUP));
        if (alt) sequence.Add(Key(VkMenu, KEYEVENTF_KEYUP));
        if (shift) sequence.Add(Key(VkShift, KEYEVENTF_KEYUP));
        if (ctrl) sequence.Add(Key(VkControl, KEYEVENTF_KEYUP));

        // 3) 恢复仍然物理按住的修饰键，让系统键态与物理键盘一致（用户松开时自然抵消）
        foreach (uint modVk in held)
            sequence.Add(Key(modVk, 0));

        var inputs = new INPUT[sequence.Count];
        for (int i = 0; i < sequence.Count; i++)
            inputs[i] = Input(sequence[i]);
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            Log.Error("组合键发送失败（VK {Vk}），错误码 {ErrorCode}", vk, Marshal.GetLastWin32Error());
        else
            Log.Debug("组合键已发送（VK {Vk}，Ctrl={Ctrl} Shift={Shift} Alt={Alt}，先抬起 {Held} 个物理修饰键）",
                vk, ctrl, shift, alt, held.Count);
    }

    private static void RunHookLoop()
    {
        _threadId = GetCurrentThreadId();
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, HookCallbackProc, GetModuleHandle(null), 0);
        if (_hookHandle == IntPtr.Zero)
        {
            Log.Error("键盘事件钩子安装失败，错误码 {ErrorCode}", Marshal.GetLastWin32Error());
            return;
        }
        Log.Debug("键盘事件钩子已安装（线程 {ThreadId}）", _threadId);
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
            Log.Debug("键盘事件钩子已卸载");
        }
    }

    private static IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _filter is not null)
        {
            try
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                // 注入事件（含自己回放的）与抑制期事件一律放行，不询问过滤器
                bool injected = (data.flags & (LLKHF_INJECTED | LLKHF_LOWERIL_INJECTED)) != 0
                                || data.dwExtraInfo == ReplayMarker;
                if (!injected && !RadialGuard.IsSuppressed)
                {
                    var message = (long)wParam switch
                    {
                        WM_KEYDOWN => KeyHookMessage.KeyDown,
                        WM_KEYUP => KeyHookMessage.KeyUp,
                        WM_SYSKEYDOWN => KeyHookMessage.SysKeyDown,
                        WM_SYSKEYUP => KeyHookMessage.SysKeyUp,
                        _ => (KeyHookMessage?)null,
                    };
                    if (message is { } msg)
                    {
                        foreach (Func<KeyHookMessage, uint, bool> f in _filter.GetInvocationList())
                            if (f(msg, data.vkCode))
                                return new IntPtr(1); // 吞下：不放给系统
                    }
                }
            }
            catch
            {
                // 绝不让异常逃逸进本机回调；异常按放行处理
            }
        }
        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private static KEYBDINPUT Key(uint vk, uint flags) => new() { WVk = (ushort)vk, DwFlags = flags, DwExtraInfo = ReplayMarker };
    private static INPUT Input(KEYBDINPUT ki) => new() { Type = 1, U = new INPUTUNION { Ki = ki } };

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

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public nuint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public nuint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type; // 1 = INPUT_KEYBOARD
        public INPUTUNION U;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    #endregion
}
