using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Serilog;

namespace LesTool.Services;

public class HotkeyBinding
{
    /// <summary>规范形式 "Ctrl+Alt+A"：修饰键按 Ctrl/Alt/Shift/Win 顺序，主键为 Key 枚举名，可原样解析回来。</summary>
    public string Keys { get; set; } = "";

    /// <summary>ActionRegistry 的命令 Id。</summary>
    public string Action { get; set; } = "";

    public bool Enabled { get; set; } = true;
}

public enum HotkeyRegState
{
    Registered,

    /// <summary>注册失败：被其他程序占用，或为系统保留组合（如 Win+L）。</summary>
    Conflict
}

/// <summary>
/// 全局热键的注册与分发。UI 线程上建消息窗口收 WM_HOTKEY。
/// Windows 无法枚举他人注册的热键，占用情况只能以真实注册的结果为准：
/// ApplyAll 后经 StateOf 查询，失败的条目由页面如实标警告。
/// </summary>
public static class HotkeyService
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static HwndSource? _source;
    private static readonly List<HotkeyBinding> _bindings = new();
    private static readonly Dictionary<int, int> _idToIndex = new();
    private static readonly List<int> _registeredIds = new();
    // 状态按绑定行索引记，不按按键串：同一组合键配多条绑定时各行的真实结果互不覆盖
    private static readonly Dictionary<int, HotkeyRegState> _statesByIndex = new();

    /// <summary>热键触发，参数为动作 Id（UI 线程回调）。</summary>
    public static event Action<string>? ActionTriggered;

    /// <summary>在 UI 线程创建消息窗口，启动时调用一次。</summary>
    public static void Start()
    {
        if (_source != null) return;
        // ParentWindow = -3 即 HWND_MESSAGE：消息专用窗口，不可见、不进任务栏
        _source = new HwndSource(new HwndSourceParameters("LesTool.Hotkey") { ParentWindow = new IntPtr(-3) });
        _source.AddHook(WndProc);
        Log.Debug("热键消息窗口就绪");
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey
            && _idToIndex.TryGetValue(wParam.ToInt32(), out var index)
            && index >= 0 && index < _bindings.Count)
        {
            var binding = _bindings[index];
            Log.Debug("热键触发：{Keys} → {Action}", binding.Keys, binding.Action);
            ActionTriggered?.Invoke(binding.Action);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>全量重注册。绑定数很少，全注销再注册最简单，也顺带留下每条的真实注册状态。
    /// 同一组合键出现多条绑定时，首条正常注册，其余直接判冲突（Windows 不允许重复注册）。</summary>
    public static void ApplyAll(IEnumerable<HotkeyBinding> bindings)
    {
        var hwnd = _source?.Handle ?? IntPtr.Zero;
        foreach (var id in _registeredIds)
            UnregisterHotKey(hwnd, id);
        _registeredIds.Clear();
        _idToIndex.Clear();
        _statesByIndex.Clear();
        _bindings.Clear();
        _bindings.AddRange(bindings);

        var seenCombos = new HashSet<(uint Modifiers, uint Vk)>();
        for (var i = 0; i < _bindings.Count; i++)
        {
            var binding = _bindings[i];
            if (!binding.Enabled || string.IsNullOrWhiteSpace(binding.Keys)) continue;
            if (!TryParseKeys(binding.Keys, out var modifiers, out var vk))
            {
                Log.Warning("热键解析失败，跳过：{Keys}", binding.Keys);
                continue;
            }
            if (!seenCombos.Add((modifiers, vk)))
            {
                _statesByIndex[i] = HotkeyRegState.Conflict;
                Log.Warning("热键组合键重复，本条不注册（先到者生效）：{Keys}", binding.Keys);
                continue;
            }

            var id = i + 1;
            if (RegisterHotKey(hwnd, id, modifiers | ModNoRepeat, vk))
            {
                _registeredIds.Add(id);
                _idToIndex[id] = i;
                _statesByIndex[i] = HotkeyRegState.Registered;
            }
            else
            {
                _statesByIndex[i] = HotkeyRegState.Conflict;
                Log.Warning("热键注册失败（Win32 错误 {Error}），可能被其他程序占用或为系统保留组合：{Keys}",
                    Marshal.GetLastWin32Error(), binding.Keys);
            }
        }
    }

    /// <summary>某条绑定的实际注册状态；null 表示未注册（键为空或行已停用）。</summary>
    public static HotkeyRegState? StateOf(HotkeyBinding binding) =>
        binding.Enabled && _bindings.IndexOf(binding) is var index && index >= 0
        && _statesByIndex.TryGetValue(index, out var state)
            ? state
            : null;

    /// <summary>按规范顺序拼出可持久化、可回解析的快捷键串。</summary>
    public static string BuildKeys(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>允许无修饰键单独绑定的键（F 区/截屏类/媒体类）；普通键必须带修饰键，否则会全局拦掉打字。</summary>
    public static bool AllowedWithoutModifier(Key key)
    {
        var k = (int)key;
        if (k >= (int)Key.F1 && k <= (int)Key.F12) return true;
        var name = key.ToString();
        return key is Key.PrintScreen or Key.Print or Key.Scroll or Key.Pause or Key.Insert
               || name.StartsWith("Media", StringComparison.Ordinal)
               || name.StartsWith("Volume", StringComparison.Ordinal)
               || name.StartsWith("Launch", StringComparison.Ordinal)
               || name.StartsWith("Browser", StringComparison.Ordinal);
    }

    private static bool TryParseKeys(string keys, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (var segment in keys.Split('+'))
        {
            switch (segment.Trim())
            {
                case "Ctrl":
                    modifiers |= ModControl;
                    continue;
                case "Alt":
                    modifiers |= ModAlt;
                    continue;
                case "Shift":
                    modifiers |= ModShift;
                    continue;
                case "Win":
                    modifiers |= ModWin;
                    continue;
            }

            if (vk != 0 || !Enum.TryParse<Key>(segment.Trim(), out var key)) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        }

        return vk != 0;
    }
}
