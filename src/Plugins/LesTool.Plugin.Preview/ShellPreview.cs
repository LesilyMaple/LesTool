using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Serilog;

namespace LesTool.Plugin.Preview;

/// <summary>
/// 系统预览句柄插座（三层瀑布第一层）：借用 Windows 注册的 Shell 预览句柄
/// （资源管理器预览窗格同款引擎）在预览窗内渲染。装了 Office/WPS 即获得
/// Word/Excel 原生效果；本机现有的 PDF/RTF 句柄直接受益。
/// COM 布局一律以本机 SDK 头文件为准（Core Audio 同纪律，shobjidl_core.h/propsys.h
/// 10.0.26100 实核）：IPreviewHandler vtable = SetWindow, SetRect, DoPreview, Unload,
/// SetFocus, QueryFocus, TranslateAccelerator（注：无 SetFrame，改矩形走 SetRect）。
/// 生命周期：注册表查 CLSID → CoCreateInstance → IInitializeWithFile（退
/// IInitializeWithStream）→ HwndHost 建子窗时 SetWindow+DoPreview；Pause/窗口
/// 收起时 Unload 释放。任一步失败返回 null，调度器落自绘/信息卡，绝不抛出。
/// </summary>
internal static class ShellPreview
{
    /// <summary>Shell 预览句柄的注册表关联键（shellex 下的固定 GUID）。</summary>
    private const string HandlerAssociationKey = @"shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    private static readonly Guid IidPreviewHandler = new("8895B1C6-B41F-4C1C-A562-0D564250836F");
    private static readonly Guid IidInitializeWithFile = new("B7D14566-0509-4CCE-A71F-0A554233BD9B");
    private static readonly Guid IidInitializeWithStream = new("B824B49D-22AC-4161-AC8A-9916E8FA3F7F");

    private const uint ClsctxInprocServer = 0x1;
    private const uint StgmRead = 0;
    private const int S_OK = 0;

    /// <summary>扩展名 → 句柄 CLSID 缓存（含"无句柄"负结果，注册表只查一次）。</summary>
    private static readonly Dictionary<string, Guid?> ClsidCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>句柄预览的固定内容尺寸（DIP）：文档类内容页数不定，不做内容自适应。</summary>
    public static readonly Size ContentSizeDip = new(480, 640);

    /// <summary>
    /// 尝试用系统句柄渲染文件。扩展名无关联句柄、COM 创建或初始化失败均返回 null。
    /// 文件 IO 只发生在 DoPreview（宿主建窗时），此处调用本身轻量。
    /// </summary>
    public static PreviewContent? TryCreate(string path)
    {
        try
        {
            if (new FileInfo(path).Length > OfficeDocument.MaxFileBytes)
                return null; // 超大文件护栏：句柄同步渲染会卡 UI，直接走自绘/信息卡路径

            Guid? clsid = ResolveClsid(Path.GetExtension(path));
            if (clsid is not { } handlerId)
                return null;

            Guid id = handlerId;
            var handler = CoCreateInstance(in id, IntPtr.Zero, ClsctxInprocServer, in IidPreviewHandler);
            if (!TryInitialize(handler, path))
            {
                Marshal.FinalReleaseComObject(handler);
                return null;
            }

            var host = new ShellPreviewHost(handler);
            Log.Debug("系统预览句柄已就绪：{Path}（CLSID {Clsid}）", path, handlerId);
            return new PreviewContent(host, ContentSizeDip, host.Shutdown);
        }
        catch (Exception ex)
        {
            // 单个句柄崩坏不拖累整体：降级到自绘/信息卡
            Log.Debug(ex, "系统预览句柄创建失败，走自绘降级：{Path}", path);
            return null;
        }
    }

    private static Guid? ResolveClsid(string ext)
    {
        if (ClsidCache.TryGetValue(ext, out Guid? cached)) return cached;
        Guid? result = null;
        try
        {
            using RegistryKey? key = Registry.ClassesRoot.OpenSubKey(ext + @"\" + HandlerAssociationKey);
            if (key?.GetValue(null) is string clsid && Guid.TryParse(clsid, out Guid guid))
                result = guid;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "预览句柄注册表查询失败：{Ext}", ext);
        }
        ClsidCache[ext] = result;
        return result;
    }

    /// <summary>初始化：优先 IInitializeWithFile，句柄不支持再退 IInitializeWithStream。</summary>
    private static bool TryInitialize(IPreviewHandler handler, string path)
    {
        IntPtr unknown = Marshal.GetIUnknownForObject(handler);
        try
        {
            if (QueryInterface(unknown, in IidInitializeWithFile, out IntPtr filePtr) == S_OK)
            {
                try
                {
                    var init = (IInitializeWithFile)Marshal.GetObjectForIUnknown(filePtr);
                    try
                    {
                        return init.Initialize(path, StgmRead) == S_OK;
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(init);
                    }
                }
                finally
                {
                    Marshal.Release(filePtr);
                }
            }

            if (QueryInterface(unknown, in IidInitializeWithStream, out IntPtr streamInitPtr) == S_OK)
            {
                try
                {
                    var init = (IInitializeWithStream)Marshal.GetObjectForIUnknown(streamInitPtr);
                    try
                    {
                        if (SHCreateStreamOnFileW(path, StgmRead, out IntPtr stream) != S_OK)
                            return false;
                        try
                        {
                            return init.Initialize(stream, StgmRead) == S_OK;
                        }
                        finally
                        {
                            Marshal.Release(stream);
                        }
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(init);
                    }
                }
                finally
                {
                    Marshal.Release(streamInitPtr);
                }
            }
            return false;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    #region COM 接口（布局以 SDK 头文件为准，勿凭记忆改动）

    [ComImport]
    [Guid("8895B1C6-B41F-4C1C-A562-0D564250836F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPreviewHandler
    {
        [PreserveSig] int SetWindow(IntPtr hwnd, in RECT prc);
        [PreserveSig] int SetRect(in RECT prc);
        [PreserveSig] int DoPreview();
        [PreserveSig] int Unload();
        [PreserveSig] int SetFocus();
        [PreserveSig] int QueryFocus(out IntPtr hwnd);
        [PreserveSig] int TranslateAccelerator(ref MSG pmsg);
    }

    [ComImport]
    [Guid("B7D14566-0509-4CCE-A71F-0A554233BD9B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile
    {
        [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [ComImport]
    [Guid("B824B49D-22AC-4161-AC8A-9916E8FA3F7F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        [PreserveSig] int Initialize(IntPtr pstream, uint grfMode); // IStream 原样指针，不做 RCW
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern IPreviewHandler CoCreateInstance(in Guid clsid, IntPtr punkOuter, uint dwClsContext, in Guid riid);

    [DllImport("ole32.dll")]
    private static extern int QueryInterface(IntPtr pObj, in Guid riid, out IntPtr ppv);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileW(string pszFileName, uint grfMode, out IntPtr ppstm);

    #endregion
}

/// <summary>
/// 预览句柄的原生子窗宿主：HwndHost 建一个 child HWND 交给句柄挂载内容
/// （SetWindow + DoPreview），布局尺寸变化转发 SetRect。Shutdown（Pause 钩子/
/// 销毁）时 Unload 释放——窗口收起即停，重开由调度器全新创建句柄。
/// </summary>
internal sealed class ShellPreviewHost : HwndHost
{
    private readonly ShellPreview.IPreviewHandler _handler; // 声明在 ShellPreview 内、internal 可见性
    private IntPtr _childHwnd;

    internal ShellPreviewHost(ShellPreview.IPreviewHandler handler)
    {
        _handler = handler;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _childHwnd = CreateWindowEx(
            0, "STATIC", string.Empty,
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, 0, 0,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_childHwnd == IntPtr.Zero)
            throw new InvalidOperationException("预览句柄宿主子窗创建失败");

        var rc = new ShellPreview.RECT();
        if (_handler.SetWindow(_childHwnd, in rc) == 0)
        {
            SizeChanged += (_, _) => PushRect();
            if (_handler.DoPreview() != 0)
                Log.Debug("预览句柄 DoPreview 返回失败");
            // 首次布局完成后再推一次真实尺寸（BuildWindowCore 时还是 0×0）
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, PushRect);
        }
        else
        {
            Log.Debug("预览句柄 SetWindow 失败");
        }
        return new HandleRef(this, _childHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Shutdown();
        DestroyWindow(hwnd.Handle);
    }

    /// <summary>收起/换内容时停摆：Unload 后句柄资源全放（幂等，可重复调）。</summary>
    internal void Shutdown()
    {
        try
        {
            _handler.Unload();
            Marshal.FinalReleaseComObject(_handler);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "预览句柄 Unload 释放异常（已忽略）");
        }
    }

    private void PushRect()
    {
        var rc = new ShellPreview.RECT
        {
            Left = 0,
            Top = 0,
            Right = (int)ActualWidth,
            Bottom = (int)ActualHeight,
        };
        _handler.SetRect(in rc);
    }

    private const long WS_CHILD = 0x40000000L;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_CLIPCHILDREN = 0x02000000L;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        long dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
