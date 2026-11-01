using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace LesTool.Services;

/// <summary>
/// 轮盘场景解析：按前台窗口进程名在设置里找应用场景，合成"全局默认 + 应用覆盖"的生效槽位。
/// 两条消费路径刻意分开：
/// - 钩子线程的禁用闸（IsForegroundDisabled）：只读 volatile 快照（禁用进程名集合），
///   无锁、无 IO，LowLevelHooksTimeout 纪律之内；
/// - UI 线程的槽位合成（EffectiveActions）：直接读 settings（所有编辑都发生在 UI 线程）。
/// 前台进程名按 HWND 缓存；缓存字段可能被钩子/UI 两线程先后写，最坏情况是短暂用到
/// 旧值（引用赋值原子、string 不可变），无撕裂风险。
/// </summary>
internal static class RadialSceneResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static volatile HashSet<string> _disabledSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private static IntPtr _cachedHwnd;
    private static string? _cachedProcess;

    /// <summary>本应用 exe 文件名（小写，与场景键同格式），枚举清单里永远不列自己。</summary>
    private static readonly string? SelfImage =
        Path.GetFileName(Environment.ProcessPath ?? "").ToLowerInvariant();

    /// <summary>系统噪音（小写进程名，不含扩展名）：当前登录会话里也在跑、但建场景无意义的
    /// 系统宿主/服务壳（任务管理器"应用"区同样不收这些）。非穷举，跨会话的靠 SessionId 过滤。</summary>
    private static readonly HashSet<string> SystemNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost", "audiodg", "backgroundtaskhost", "conhost", "csrss", "ctfmon",
        "dllhost", "dwm", "fontdrvhost", "idle", "logonui", "lsass", "memory compression",
        "msmpeng", "nissrv", "openconsole", "progman", "registry", "runtimebroker", "searchapp",
        "searchhost", "searchindexer", "searchui", "securityhealthservice", "securityhealthsystray",
        "shellexperiencehost", "sihost", "smartscreen", "smss", "startmenuexperiencehost",
        "system", "taskhost", "taskhostw", "textinputhost", "vmmem", "vmmemws", "werfault",
        "wermgr", "wininit", "winlogon", "workerw", "wslhost", "wudfhost",
    };

    /// <summary>设置变更后刷新禁用快照（UI 线程；启动与每次保存后调用）。</summary>
    public static void RefreshSnapshot()
    {
        var snapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scene in App.Settings.Radial.Scenes)
            if (scene.Disabled && !string.IsNullOrEmpty(scene.Process))
                snapshot.Add(scene.Process!);
        _disabledSnapshot = snapshot; // 整体换引用：钩子线程读到的永远是完整集合
        Log.Debug("轮盘禁用快照已刷新：{Count} 个软件", snapshot.Count);
    }

    /// <summary>禁用闸（钩子线程）：当前前台软件是否禁用了轮盘。须快速返回。</summary>
    public static bool IsForegroundDisabled()
    {
        var process = ForegroundProcessName();
        return process != null && _disabledSnapshot.Contains(process);
    }

    /// <summary>前台窗口进程名（小写文件名，如 "msedge"）；识别失败返回 null。</summary>
    public static string? ForegroundProcessName()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        if (hwnd == _cachedHwnd) return _cachedProcess;

        var name = ProcessNameOfWindow(hwnd);
        _cachedHwnd = hwnd;
        _cachedProcess = name;
        return name;
    }

    /// <summary>窗口所属进程名（小写文件名）；失败返回 null。</summary>
    public static string? ProcessNameOfWindow(IntPtr hwnd) =>
        ImagePathOfWindow(hwnd) is { } path ? Path.GetFileName(path).ToLowerInvariant() : null;

    /// <summary>窗口所属进程的完整镜像路径；失败返回 null。</summary>
    private static string? ImagePathOfWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowThreadProcessId(hwnd, out uint pid) || pid == 0)
            return null;
        return ImagePathOfPid(pid);
    }

    /// <summary>按 PID 取完整镜像路径；打不开（权限不足/已退出）返回 null。</summary>
    private static string? ImagePathOfPid(uint pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            return QueryFullProcessImageName(process, 0, buffer, out uint length)
                ? buffer.ToString()
                : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>按进程名找应用场景；无则 null（UI 线程）。</summary>
    public static RadialSceneSettings? FindAppScene(string? process) =>
        process is null
            ? null
            : App.Settings.Radial.Scenes.FirstOrDefault(s =>
                string.Equals(s.Process, process, StringComparison.OrdinalIgnoreCase));

    /// <summary>前台软件对应的应用场景（无则 null；UI 线程）。</summary>
    public static RadialSceneSettings? ForegroundScene() =>
        FindAppScene(ForegroundProcessName());

    /// <summary>可添加应用清单的条目：Process=场景键（小写 exe 文件名，与前台识别同格式）；
    /// Display=展示名（版本信息里的友好名，读不到就用文件名）；Subtitle=第二行（进程名 · 窗口标题）。</summary>
    public sealed record AppCandidate(string Process, string Display, string Subtitle);

    /// <summary>枚举可建场景的应用（对齐任务管理器"应用"区）：有可见窗口的运行中程序，按 exe 去重。
    /// 窗口归属走"窗口→进程号 + 进程快照"对账，不经 OpenProcess——管理员权限的窗口、
    /// 其他虚拟桌面上的窗口都能对上。键为小写 exe 文件名，按展示名排序（UI 线程，按需调用）。</summary>
    public static List<AppCandidate> EnumerateApps()
    {
        var windowTitles = CollectWindowTitles(); // pid → 首个窗口标题
        var result = new List<AppCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentSession = Process.GetCurrentProcess().SessionId;
        var skipped = 0;

        foreach (var entry in Process.GetProcesses())
        {
            using var process = entry;
            string raw;
            int session;
            try
            {
                raw = process.ProcessName;
                session = process.SessionId;
            }
            catch (InvalidOperationException)
            {
                skipped++; // 枚举瞬间已退出的短命进程
                continue;
            }

            if (raw.Length == 0 || SystemNoise.Contains(raw) || session != currentSession)
            {
                skipped++;
                continue;
            }

            if (!windowTitles.TryGetValue(process.Id, out var title))
            {
                skipped++; // 无可见窗口：任务管理器归"后台进程"，不进此清单
                continue;
            }

            var name = (raw + ".exe").ToLowerInvariant();
            if (name == SelfImage || !seen.Add(name)) continue; // 同名多进程（Edge 等）只列一条

            var description = DescriptionOf(process.Id);
            var display = string.IsNullOrWhiteSpace(description) ? name : description.Trim();
            var subtitle = string.Equals(display, name, StringComparison.OrdinalIgnoreCase)
                ? title
                : $"{name} · {title}";
            result.Add(new AppCandidate(name, display, subtitle));
        }

        result.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        Log.Debug("应用枚举：窗口扫描命中 {WindowPids} 个窗口进程，可列 {Listed} 个，滤无窗口/系统/跨会话 {Skipped} 个",
            windowTitles.Count, result.Count, skipped);
        return result;
    }

    /// <summary>可见、有标题的顶层窗口 → 进程号首个窗口标题。只取进程号不做归属（归属交给
    /// 进程快照对账），因此不需要打开对方进程的权限；不查 cloak——其他虚拟桌面上的
    /// 窗口任务管理器也照样算"应用"。</summary>
    private static Dictionary<int, string> CollectWindowTitles()
    {
        var titles = new Dictionary<int, string>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            var title = new StringBuilder(256);
            if (GetWindowText(hwnd, title, 256) == 0) return true;
            var className = new StringBuilder(64);
            GetClassName(hwnd, className, 64);
            if (className.ToString() is "Progman" or "WorkerW") return true; // 桌面壁纸/图标层

            if (!GetWindowThreadProcessId(hwnd, out uint pid) || pid == 0) return true;
            if (!titles.ContainsKey((int)pid)) titles[(int)pid] = title.ToString();
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    /// <summary>exe 的友好描述（版本信息 FileDescription，如"微信"）；受保护/已退出/
    /// 无版本资源时返回 null，展示名回退文件名。</summary>
    private static string? DescriptionOf(int pid)
    {
        try
        {
            var path = ImagePathOfPid((uint)pid);
            return path is null ? null : FileVersionInfo.GetVersionInfo(path).FileDescription;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>合成实际生效的 24 槽命令（Quicker 同款规则）：内外圈（0-15）应用槽
    /// Override=true 用自身（null=该软件下此槽禁用），否则回落全局同位槽；
    /// 扩展圈（16-23）仅全局场景生效，应用场景不覆盖（UI 也不提供编辑）。</summary>
    public static List<string?> EffectiveActions(RadialSceneSettings? scene)
    {
        var globalSlots = App.Settings.Radial.Global.Slots;
        var result = new List<string?>(RadialSettingsData.SlotCount);
        for (var i = 0; i < RadialSettingsData.SlotCount; i++)
        {
            if (i >= RadialSettingsData.ExtensionFirst)
            {
                result.Add(globalSlots[i].Action);
                continue;
            }

            var slot = scene?.Slots[i];
            result.Add(slot is { Override: true } ? slot.Action : globalSlots[i].Action);
        }
        return result;
    }

    #region P/Invoke

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags,
        StringBuilder exeName, out uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    #endregion
}
