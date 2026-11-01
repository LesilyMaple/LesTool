using System.Runtime.InteropServices;

namespace LesTool.Plugin.Volume;

/// <summary>
/// Core Audio COM 互操作（章程纪律：不引第三方音频库，手写 P/Invoke）。
///
/// 三条铁律（2026-10-12 事故教训，曾凭记忆写错四处声明、误判机器损坏整整一天）：
/// 1. 接口布局/IID 一律以本机 SDK 头文件为准：
///    C:\Program Files (x86)\Windows Kits\10\Include\&lt;ver&gt;\um\{mmdeviceapi,audiopolicy,audioclient,endpointvolume}.h
///    不靠记忆、不靠网搜摘要。
/// 2. out 接口参数封送不做 QI——必须按原生返回类型声明（GetSession 返回基接口），
///    取派生接口用显式 cast（cast 才 QI）。
/// 3. bool 参数必须显式 [MarshalAs(UnmanagedType.Bool)]（Win32 BOOL 4 字节）。
///
/// 本文件四个易错点（全部踩过）：IAudioSessionControl 有 9 个方法（别漏 SetGroupingParam）；
/// IAudioSessionControl2 有 5 个（别漏 SetDuckingPreference）；ISimpleAudioVolume 是
/// SetMasterVolume→GetMasterVolume→SetMute→GetMute（Set 在前！）；IAudioEndpointVolume 的
/// IID 是 5CDF2C82-841E-4546-9722-0CF74078229A（与它前两组数字撞车的 GUID 有多个，别认错）。
/// </summary>
internal static class CoreAudioInterop
{
    internal const int ClsctxAll = 0x17;
    internal const int ERender = 0;       // eRender：输出设备
    internal const int EMultimedia = 1;   // eMultimedia：媒体默认设备（与系统音量弹窗一致）
    internal const int StgmRead = 0;
    internal const int SessionStateActive = 1;
    internal const ushort VtLpwstr = 31;

    internal static readonly Guid IidIAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    internal static readonly Guid IidIAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    /// <summary>设备友好名（“扬声器 (Realtek …)”就是它），DEVPKEY_Device_FriendlyName。</summary>
    internal static PropertyKey PkeyDeviceFriendlyName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    internal static IMMDeviceEnumerator CreateEnumerator() =>
        (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();

    // ---------- 图标提取（shell32） ----------
    internal const uint ShgfiIcon = 0x100;
    internal const uint ShgfiLargeIcon = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShfileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SHGetFileInfoW(string path, uint attributes,
        ref ShfileInfo info, uint size, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    // ---------- 提权进程的 exe 路径（游戏反作弊以管理员运行，MainModule 读不了） ----------
    internal const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags,
        System.Text.StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsctx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

/// <summary>设备名只读 VT_LPWSTR 一种；显式补齐到 24 字节，与原生 PROPVARIANT 对齐。</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public IntPtr Pointer;
    [FieldOffset(16)] public long Padding;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid Fmtid;
    public int Pid;

    public PropertyKey(Guid fmtid, int pid)
    {
        Fmtid = fmtid;
        Pid = pid;
    }
}

internal static class Ole32
{
    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PropVariant value);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr client);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr client);
    [PreserveSig] int GetChannelCount(out uint channels);
    [PreserveSig] int SetMasterVolumeLevel(float decibels, Guid context);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, Guid context);
    [PreserveSig] int GetMasterVolumeLevel(out float decibels);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float decibels, Guid context);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, Guid context);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float decibels);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid context);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
    [PreserveSig] int VolumeStepUp(Guid context);
    [PreserveSig] int VolumeStepDown(Guid context);
    [PreserveSig] int QueryHardwareSupport(out uint support);
    [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    // 前两法继承自 IAudioSessionManager（vtable 占位，未用）
    [PreserveSig] int GetAudioSessionControl(ref Guid sessionGuid, int flags, out IntPtr control);
    [PreserveSig] int GetSimpleAudioVolume(ref Guid sessionGuid, int flags, out IntPtr volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    [PreserveSig] int RegisterSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterSessionNotification(IntPtr client);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string? sessionId, IntPtr client);
    [PreserveSig] int UnregisterDuckNotification(IntPtr client);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    // 原生返回基接口 IAudioSessionControl（out 接口参数封送不做 QI，勿声明成派生接口）
    [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
}

[ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid context);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid context);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid context);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
}

// ⚠️ ComImport 接口继承不参与 vtable 布局：写 `: IAudioSessionControl` 时派生方法会从基类
// 起点重排、全部落到错误的基类方法上（本次事故根因之一）。派生接口必须平铺声明全部方法。
[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid context);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid context);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid context);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionId);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string instanceId);
    [PreserveSig] int GetProcessId(out uint processId);
    /// <summary>S_OK(0)=系统提示音会话，S_FALSE(1)=普通会话。</summary>
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    // 注意顺序：Set 在 Get 前（与直觉相反，头文件原文如此）
    [PreserveSig] int SetMasterVolume(float level, Guid context);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid context);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}
