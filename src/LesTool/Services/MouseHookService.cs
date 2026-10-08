using System.Runtime.InteropServices;
using Serilog;

namespace LesTool.Services;

/// <summary>
/// WH_MOUSE_LL 低级鼠标钩子：在专属线程安装并跑消息泵
/// （系统要求安装钩子的线程持续取消息，否则钩子被摘除）。
/// 回调只喂 RadialTrigger 纯计算，不做 IO / 加锁等待，避免触发 LowLevelHooksTimeout。
/// 始终 CallNextHookEx 不吞事件：普通右键点击不受影响；
/// 后续做手势吞键时再按需拦截。
/// </summary>
public sealed class MouseHookService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_QUIT = 0x0012;

    private readonly RadialTrigger _trigger;
    private readonly HookProc _hookProc; // 字段持引用：委托被 GC 后回调悬空会导致进程崩溃
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hookHandle;

    public MouseHookService(RadialTrigger trigger)
    {
        _trigger = trigger;
        _hookProc = HookCallback;
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
            Log.Debug("鼠标钩子已卸载");
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var point = new System.Windows.Point(data.pt.X, data.pt.Y);
                switch ((long)wParam)
                {
                    case WM_RBUTTONDOWN:
                        _trigger.OnRightButtonDown(point);
                        break;
                    case WM_MOUSEMOVE:
                        _trigger.OnMouseMove(point);
                        break;
                    case WM_RBUTTONUP:
                        _trigger.OnRightButtonUp(point);
                        break;
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
