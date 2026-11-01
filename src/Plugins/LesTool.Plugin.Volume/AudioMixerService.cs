using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace LesTool.Plugin.Volume;

/// <summary>混音器里的一个应用组：同一 pid 的全部活动会话合成一行（EarTrumpet 式按应用调节）。</summary>
internal sealed class MixerApp
{
    public required string Key { get; init; }          // "sys" / exe 路径 / unk-对象指针
    public required string DisplayName { get; init; }
    public string? ExePath { get; init; }
    public required bool IsSystem { get; init; }
    public float Volume { get; set; }                  // 0..1
    public bool Mute { get; set; }
    /// <summary>音量写入是否有效（防御性 UI：写失败即停用该行滑块，静音仍可用）。</summary>
    public bool VolumeWritable { get; set; } = true;
    public List<ISimpleAudioVolume> Targets { get; } = new();
}

/// <summary>
/// 音量混音器数据层：默认输出设备主音量 + 活动会话按 pid 分组。
/// 会话列表没有可靠的变更通知（sndvol/EarTrumpet 同样靠轮询），这里每次
/// Refresh 全量重建模型；可见时由 UI 以 250ms 轮询驱动，COM 对象交 GC 回收。
/// </summary>
public sealed class AudioMixerService : IDisposable
{
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private bool _warnedNoDevice;
    private bool _warnedActivate;

    internal string DeviceName { get; private set; } = "默认输出设备";
    internal string DeviceId { get; private set; } = "";
    internal float MasterVolume { get; private set; }
    internal bool MasterMute { get; private set; }
    /// <summary>本机是否拿得到 IAudioEndpointVolume。开发机（22H2 RTM 无补丁）上
    /// 该接口被系统对所有设备返回 E_NOINTERFACE——拿不到时 UI 隐藏主音量行，应用行照常。</summary>
    internal bool MasterAvailable { get; private set; }
    internal List<MixerApp> Apps { get; } = new();

    /// <summary>启动时创建 COM 枚举器；失败不抛（无声环境也要能跑起来）。</summary>
    public void Initialize()
    {
        try
        {
            _enumerator = CoreAudioInterop.CreateEnumerator();
            Log.Information("Core Audio 枚举器已创建");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Core Audio 枚举器创建失败，音量混音器不可用");
        }
    }

    /// <summary>全量刷新模型。任何一步失败保留上次状态（界面继续显示旧值而非清空）。</summary>
    public void Refresh()
    {
        if (_enumerator is null) return;

        try
        {
            int hr = _enumerator.GetDefaultAudioEndpoint(
                CoreAudioInterop.ERender, CoreAudioInterop.EMultimedia, out var device);
            if (hr != 0 || device is null)
            {
                if (!_warnedNoDevice)
                {
                    Log.Warning("无可用输出设备（hr=0x{Hr:X8}），混音器保持上次状态", hr);
                    _warnedNoDevice = true;
                }
                return;
            }
            _warnedNoDevice = false;

            device.GetId(out var deviceId);
            if (deviceId != DeviceId)
            {
                DeviceId = deviceId;
                DeviceName = ReadFriendlyName(device) ?? "默认输出设备";
                Log.Information("默认输出设备：{Name}", DeviceName);
            }

            // 主音量接口激活失败不能拖垮会话枚举（应用行与主音量是两条独立通道）
            var iidVolume = CoreAudioInterop.IidIAudioEndpointVolume;
            if (TryActivate(device, ref iidVolume, out var endpointObj))
            {
                _endpoint = (IAudioEndpointVolume)endpointObj;
                if (_endpoint.GetMasterVolumeLevelScalar(out var master) == 0)
                    MasterVolume = Math.Clamp(master, 0f, 1f);
                if (_endpoint.GetMute(out var masterMute) == 0)
                    MasterMute = masterMute;
                MasterAvailable = true;
            }
            else
            {
                _endpoint = null;
                MasterAvailable = false;
            }

            var iidManager = CoreAudioInterop.IidIAudioSessionManager2;
            if (!TryActivate(device, ref iidManager, out var managerObj)) return;
            var manager = (IAudioSessionManager2)managerObj;
            if (manager.GetSessionEnumerator(out var sessions) != 0 || sessions is null)
            {
                if (!_warnedActivate)
                {
                    Log.Error("会话枚举器获取失败（保留上次状态）");
                    _warnedActivate = true;
                }
                return;
            }
            sessions.GetCount(out var count);

            var groups = new Dictionary<string, MixerApp>();
            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var control) != 0) continue;
                if (!TryBuildGroup(control, groups)) continue;
            }

            Apps.Clear();
            Apps.AddRange(groups.Values
                .OrderBy(a => a.IsSystem ? 0 : 1)   // 系统声音固定首行
                .ThenBy(a => a.DisplayName, StringComparer.CurrentCulture));
            _warnedActivate = false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "音频状态刷新失败");
        }
    }

    /// <summary>激活设备接口；失败记一次 Error（带 HRESULT，4Hz 轮询节流）并保留上次状态。</summary>
    private bool TryActivate(IMMDevice device, ref Guid iid, out object activated)
    {
        int hr = device.Activate(ref iid, CoreAudioInterop.ClsctxAll, IntPtr.Zero, out activated);
        if (hr == 0 && activated is not null) return true;

        activated = null!;
        if (!_warnedActivate)
        {
            Log.Error("音频接口激活失败：iid={Iid}，hr=0x{Hr:X8}（保留上次状态）", iid.ToString(), hr);
            _warnedActivate = true;
        }
        return false;
    }

    /// <summary>单个会话 → 应用组（pid 分组 + IsSystemSoundsSession，官方正道路径）。</summary>
    private static bool TryBuildGroup(IAudioSessionControl control, Dictionary<string, MixerApp> groups)
    {
        try
        {
            if (control.GetState(out var state) != 0 || state != CoreAudioInterop.SessionStateActive)
                return false;

            // GetSession 返回基接口；cast 到平铺声明的 Control2 走 QI
            if (control is not IAudioSessionControl2 control2) return false;
            var isSystem = control2.IsSystemSoundsSession() == 0;

            string key;
            string name;
            string? exePath = null;
            if (isSystem)
            {
                key = "sys";
                name = "系统声音";
            }
            else
            {
                if (control2.GetProcessId(out var pid) != 0) return false;
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    key = "pid-" + pid;
                    name = process.ProcessName;
                    exePath = GetExecutablePath(process, pid);
                }
                catch (ArgumentException)
                {
                    return false; // 枚举与查询之间进程已退出
                }
            }

            if (!(control is ISimpleAudioVolume volume)) return false;
            if (volume.GetMasterVolume(out var level) != 0) level = 1f;
            if (volume.GetMute(out var mute) != 0) mute = false;

            if (!groups.TryGetValue(key, out var group))
            {
                group = new MixerApp
                {
                    Key = key,
                    DisplayName = name,
                    ExePath = exePath,
                    IsSystem = isSystem,
                    Volume = Math.Clamp(level, 0f, 1f),
                    Mute = mute,
                };
                groups[key] = group;
            }
            group.Targets.Add(volume);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "会话解析失败，已跳过");
            return false;
        }
    }

    private static readonly HashSet<uint> _limitedQueryLogged = new();

    /// <summary>
    /// 提权/受保护进程（如游戏反作弊）读不了 MainModule，
    /// 退回 OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) + QueryFullProcessImageNameW 查完整路径（无需管理员）。
    /// </summary>
    private static string? GetExecutablePath(Process process, uint pid)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path)) return path;
        }
        catch
        {
            // MainModule 对提权进程抛 Access Denied，走系统调用退路
        }

        var handle = CoreAudioInterop.OpenProcess(CoreAudioInterop.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            if (_limitedQueryLogged.Add(pid))
                Log.Debug("进程 {Pid} MainModule 与 LIMITED_INFORMATION 均取不到路径（OpenProcess 失败）", pid);
            return null;
        }
        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            uint size = 1024;
            if (CoreAudioInterop.QueryFullProcessImageNameW(handle, 0, buffer, ref size))
            {
                if (_limitedQueryLogged.Add(pid))
                    Log.Debug("进程 {Pid} 经 LIMITED_INFORMATION 取到路径 {Path}", pid, buffer.ToString());
                return buffer.ToString();
            }
            return null;
        }
        finally
        {
            CoreAudioInterop.CloseHandle(handle);
        }
    }

    private static string? ReadFriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(CoreAudioInterop.StgmRead, out var store) != 0)
                return null;
            var key = CoreAudioInterop.PkeyDeviceFriendlyName;
            if (store.GetValue(ref key, out var value) != 0) return null;

            string? name = null;
            if (value.Vt == CoreAudioInterop.VtLpwstr && value.Pointer != IntPtr.Zero)
                name = Marshal.PtrToStringUni(value.Pointer);
            Ole32.PropVariantClear(ref value);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "设备名读取失败，用默认名");
            return null;
        }
    }

    // ---------- 写入（UI 动作入口；成功后同步模型，等下一轮轮询校正） ----------

    public void SetMasterVolume(float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        try
        {
            if (_endpoint?.SetMasterVolumeLevelScalar(level, Guid.Empty) == 0)
                MasterVolume = level;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "主音量设置失败：{Pct}%", (int)Math.Round(level * 100));
        }
    }

    public void SetMasterMute(bool mute)
    {
        try
        {
            if (_endpoint?.SetMute(mute, Guid.Empty) == 0)
                MasterMute = mute;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "主音量静音设置失败：{Mute}", mute);
        }
    }

    public bool SetAppVolume(string key, float level)
    {
        var app = Apps.FirstOrDefault(a => a.Key == key);
        if (app is null) return false;
        level = Math.Clamp(level, 0f, 1f);
        var anySuccess = false;
        foreach (var target in app.Targets)
        {
            try
            {
                if (target.SetMasterVolume(level, Guid.Empty) == 0)
                    anySuccess = true;
                else
                    Log.Debug("会话音量写入失败：{App}，hr 非 0", app.DisplayName);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "会话音量写入异常：{App}", app.DisplayName);
            }
        }
        if (anySuccess) app.Volume = level;
        app.VolumeWritable = anySuccess;
        return anySuccess;
    }

    public bool SetAppMute(string key, bool mute)
    {
        var app = Apps.FirstOrDefault(a => a.Key == key);
        if (app is null) return false;
        foreach (var target in app.Targets)
        {
            try { target.SetMute(mute, Guid.Empty); }
            catch (Exception ex) { Log.Debug(ex, "会话静音设置失败：{App}", app.DisplayName); }
        }
        app.Mute = mute;
        return true;
    }
    public void Dispose()
    {
        var objects = Apps.SelectMany(app => app.Targets).Cast<object>().ToList();
        if (_endpoint is not null) objects.Add(_endpoint);
        if (_enumerator is not null) objects.Add(_enumerator);
        Apps.Clear(); _endpoint = null; _enumerator = null;
        foreach (var value in objects.Distinct(ReferenceEqualityComparer.Instance))
            try { if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
            catch (Exception error) { Log.Debug(error, "音频对象释放失败"); }
    }
}
