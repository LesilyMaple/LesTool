using System.Runtime.InteropServices;
using LesTool.Platform;
using Serilog;

namespace LesTool.Services;

/// <summary>
/// 轮盘输入源。两条通道都在专属线程（钩子回调与窗口消息共用一个消息泵，
/// 状态无需加锁）：
/// 1. WH_MOUSE_LL 低级钩子——负责"拦截"：右键按下先扣住；松开时若是普通单击，
///    把右键点击回放还给系统（SendInput 带标记），若是拖动手势则整段吞掉，
///    底下程序不弹右键菜单。滚轮仍由此转发 MouseEvents 枢纽。
/// 2. Raw Input（RIDEV_INPUTSINK）——前台无关的旁路：游戏环境过滤低级钩子
///    导致颗粒无收时，由此通道接手手势检测（游戏自身就用 Raw Input 读鼠标，
///    该通道不经过钩子链、谁也扣不走）。
/// 通道仲裁：谁先看到右键按下谁"认领"本手势，另一条只做兜底（覆盖上游
/// 工具吃掉松开事件的场景）。回放事件带 ReplayMarker 标记，两条通道都
/// 忽略，防止自拦截回环。
/// 回调只做轻事：无 IO / 无锁等待 / 无阻塞订阅方代码，避免 LowLevelHooksTimeout。
/// </summary>
public sealed class MouseHookService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_QUIT = 0x0012;
    private const uint WM_INPUT = 0x00FF;

    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEMOUSE = 0;
    private const ushort RIDEV_INPUTSINK = 0x0100;
    private const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
    private const ushort RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
    private const ushort RI_MOUSE_MOVE = 0x0800;

    private const uint LLMHF_INJECTED = 0x01;
    private const uint LLMHF_LOWERIL_INJECTED = 0x02;
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

    /// <summary>回放右键点击的 dwExtraInfo 标记；钩子与 Raw Input 都借此识别自己注入的事件。</summary>
    private static readonly nuint ReplayMarker = unchecked((nuint)0x4C4553544F4F4C21UL); // "LESTOOL!"

    private readonly RadialTrigger _trigger;
    private readonly HookProc _hookProc; // 字段持引用：委托被 GC 后回调悬空会导致进程崩溃
    private readonly WndProc _wndProc;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hookHandle;
    private IntPtr _rawHwnd;
    private ushort _rawClassAtom;

    /// <summary>当前手势由哪条通道认领。同钩子线程读写，无需加锁。</summary>
    private enum Source
    {
        None,
        Hook,
        Raw
    }

    private Source _claim = Source.None;
    private bool _hookSwallowedDown; // 钩子扣下了本次手势的物理右键按下（松开为普通单击时需回放）
    private bool _hookSawUp;         // 钩子已处理本次手势的松开（Raw 通道据此跳过）

    public MouseHookService(RadialTrigger trigger)
    {
        _trigger = trigger;
        _hookProc = HookCallback;
        _wndProc = RawWndProc;
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(RunHookLoop) { IsBackground = true, Name = "LesTool.MouseHook" };
        _thread.Start();
    }

    public void Dispose()
    {
        var thread = _thread;
        if (thread is null) return;
        _thread = null;

        if (_threadId != 0)
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(2000);
    }

    private void RunHookLoop()
    {
        _threadId = GetCurrentThreadId();
        _rawHwnd = CreateRawInputWindow();
        if (_rawHwnd != IntPtr.Zero && RegisterRawInputForMouse(_rawHwnd))
            Log.Information("Raw Input 旁路已就绪（窗口 0x{Hwnd:X}）", _rawHwnd.ToInt64());
        else
            Log.Error("Raw Input 注册失败，退回纯钩子模式（游戏内轮盘将不可用），错误码 {ErrorCode}",
                Marshal.GetLastWin32Error());

        _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hookHandle == IntPtr.Zero)
        {
            Log.Error("SetWindowsHookEx 失败，错误码 {ErrorCode}", Marshal.GetLastWin32Error());
            return;
        }
        Log.Debug("鼠标钩子已安装（线程 {ThreadId}）", _threadId);

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
            if (_rawHwnd != IntPtr.Zero)
            {
                DestroyWindow(_rawHwnd);
                _rawHwnd = IntPtr.Zero;
            }
            if (_rawClassAtom != 0)
            {
                UnregisterClass(_rawClassAtom, GetModuleHandle(null));
                _rawClassAtom = 0;
            }
            Log.Debug("鼠标钩子已卸载");
        }
    }

    #region 低级钩子：拦截 + 回放

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                if (RadialGuard.IsSuppressed)
                {
                    // 抑制期（截图会话等）：手势半途被抑制时交还通道，事件原样放行
                    if ((long)wParam == WM_RBUTTONUP && _claim == Source.Hook)
                    {
                        _claim = Source.None;
                        _hookSwallowedDown = false;
                    }
                }
                else
                {
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    // 注入事件（含自己回放的）不认领、不拦截，避免回环
                    bool injected = (data.flags & (LLMHF_INJECTED | LLMHF_LOWERIL_INJECTED)) != 0
                                    || data.dwExtraInfo == ReplayMarker;
                    if (!injected)
                    {
                        var point = new System.Windows.Point(data.pt.X, data.pt.Y);
                        switch ((long)wParam)
                        {
                            case WM_RBUTTONDOWN:
                                _claim = Source.Hook;
                                _hookSwallowedDown = true;
                                _hookSawUp = false;
                                _trigger.OnRightButtonDown(point);
                                return (IntPtr)1; // 扣住：普通单击在松开时回放，手势则整段吞掉

                            case WM_MOUSEMOVE:
                                if (_claim == Source.Hook)
                                    _trigger.OnMouseMove(point);
                                break; // 移动永不吞

                            case WM_RBUTTONUP:
                                if (_claim == Source.Hook)
                                {
                                    bool wasShown = _trigger.IsShown;
                                    _trigger.OnRightButtonUp(point);
                                    _hookSawUp = true;
                                    _claim = Source.None;
                                    if (!wasShown)
                                        ReplayRightClick(); // 普通单击：把点击还给系统
                                    return (IntPtr)1;
                                }
                                break;

                            case WM_MOUSEWHEEL:
                                // HIWORD(mouseData) 是带符号增量；封送在 MouseEvents 枢纽内完成
                                int delta = (short)((data.mouseData >> 16) & 0xFFFF);
                                MouseEvents.RaiseWheel(point, delta);
                                break;
                        }
                    }
                }
            }
            catch
            {
                // 绝不让异常逃逸进本机回调
            }
        }
        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    /// <summary>把普通右键单击回放给系统：应用照常弹右键菜单，但轮盘不参与。</summary>
    private void ReplayRightClick()
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].mi.dwFlags = MOUSEEVENTF_RIGHTDOWN;
        inputs[0].mi.dwExtraInfo = ReplayMarker;
        inputs[1].type = INPUT_MOUSE;
        inputs[1].mi.dwFlags = MOUSEEVENTF_RIGHTUP;
        inputs[1].mi.dwExtraInfo = ReplayMarker;
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            Log.Error("右键点击回放失败，错误码 {ErrorCode}", Marshal.GetLastWin32Error());
        else
            Log.Debug("普通单击已回放给系统");
    }

    #endregion

    #region Raw Input 旁路

    private IntPtr RawWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
            HandleRawInput(lParam);
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void HandleRawInput(IntPtr hRawInput)
    {
        try
        {
            if (RadialGuard.IsSuppressed)
            {
                _claim = Source.None;
                return;
            }

            uint size = 0;
            GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RAWHEADER>());
            if (size == 0 || size > 512) return;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(hRawInput, RID_INPUT, buf, ref size, (uint)Marshal.SizeOf<RAWHEADER>()) != size)
                    return;
                var raw = Marshal.PtrToStructure<RAWINPUT>(buf);
                if (raw.Header.dwType != RIM_TYPEMOUSE) return;

                var m = raw.Mouse;
                if (m.ulExtraInformation == ReplayMarker) return; // 自己回放的事件

                GetCursorPos(out var pt);
                var point = new System.Windows.Point(pt.X, pt.Y);
                ushort buttons = m.usButtonFlags;

                if ((buttons & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0 && _claim == Source.None)
                {
                    // 钩子没看到这次按下（被游戏环境过滤）→ Raw 通道认领
                    _claim = Source.Raw;
                    _hookSawUp = false;
                    _trigger.OnRightButtonDown(point);
                    Log.Debug("Raw Input 通道认领手势（钩子被过滤场景）");
                }

                if ((buttons & RI_MOUSE_MOVE) != 0 && _claim == Source.Raw)
                    _trigger.OnMouseMove(point);

                if ((buttons & RI_MOUSE_RIGHT_BUTTON_UP) != 0)
                {
                    if (_claim == Source.Raw)
                    {
                        _trigger.OnRightButtonUp(point);
                        _claim = Source.None;
                    }
                    else if (_claim == Source.Hook && !_hookSawUp)
                    {
                        // 钩子扣下了按下、松开却被上游吃掉（如其他手势工具）→ 兜底收尾
                        bool wasShown = _trigger.IsShown;
                        _trigger.OnRightButtonUp(point);
                        _claim = Source.None;
                        if (!wasShown && _hookSwallowedDown)
                            ReplayRightClick();
                        Log.Debug("松开事件被上游吞掉，Raw Input 通道兜底（手势 {WasShown}）", wasShown);
                    }
                    _hookSwallowedDown = false;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
            // 旁路只做兜底，绝不因异常干扰消息泵
        }
    }

    private IntPtr CreateRawInputWindow()
    {
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "LesToolRawInputSink",
        };
        _rawClassAtom = RegisterClassEx(ref wc);
        if (_rawClassAtom == 0)
        {
            Log.Error("Raw Input 窗口类注册失败，错误码 {ErrorCode}", Marshal.GetLastWin32Error());
            return IntPtr.Zero;
        }
        // 不可见顶层工具窗：不进任务栏/Alt+Tab，不抢焦点，专职收 WM_INPUT
        return CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            _rawClassAtom, string.Empty,
            WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
    }

    private bool RegisterRawInputForMouse(IntPtr hwnd)
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = 0x01,
            usUsage = 0x02, // 鼠标
            dwFlags = RIDEV_INPUTSINK, // 无需前台/焦点也能收到
            hwndTarget = hwnd,
        };
        return RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    #endregion

    #region P/Invoke

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
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
    private struct RAWHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWMOUSE
    {
        [FieldOffset(0)] public ushort usFlags;
        [FieldOffset(4)] public ushort usButtonFlags; // 与 ulButtons 共用偏移，取低位即按钮标志
        [FieldOffset(6)] public ushort usButtonData;
        [FieldOffset(8)] public uint ulRawButtons;
        [FieldOffset(12)] public int lLastX;
        [FieldOffset(16)] public int lLastY;
        [FieldOffset(20)] public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWHEADER Header;
        public RAWMOUSE Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public ushort dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type; // INPUT_MOUSE
        public MOUSEINPUT mi; // 键盘/硬件联合体省略：本服务只注入鼠标
    }

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumberOfDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(ushort atom, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, ushort atom, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    #endregion
}
