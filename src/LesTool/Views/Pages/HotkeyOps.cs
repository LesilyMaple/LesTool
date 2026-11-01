using System.Windows;
using System.Windows.Input;
using LesTool.Services;

namespace LesTool.Views.Pages;

/// <summary>
/// 热键操作公共层：键位查/存/重复检测/校验/格式化。2026-10-31 改版后热键配置有两个
/// 入口（热键区行、插件页头键位小部件），逻辑只有这一份。数据层走全局链路
/// （App.Settings.Hotkeys + HotkeyService.ApplyAll），一命令一键位。
/// </summary>
internal static class HotkeyOps
{
    /// <summary>当前键位（多绑定遗留取第一条；空 = 未绑定）。</summary>
    internal static string KeysOf(string commandId) =>
        App.Settings.Hotkeys.FirstOrDefault(b => b.Action.Equals(commandId, StringComparison.OrdinalIgnoreCase))?.Keys ?? "";

    /// <summary>提交某命令的键位（"" = 解绑）：一命令一键位，收敛全部旧绑定；其余命令的绑定原样保留。</summary>
    internal static void Commit(string commandId, string keys)
    {
        var others = App.Settings.Hotkeys
            .Where(b => !b.Action.Equals(commandId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (keys != "")
            others.Add(new HotkeyBinding { Keys = keys, Action = commandId, Enabled = true });
        App.Settings.Hotkeys = others;
        AppSettings.Save(App.Settings);
        HotkeyService.ApplyAll(App.Settings.Hotkeys);
    }

    /// <summary>重复检测（全局视角）：其他命令是否已占用该组合。返回占用者名，null 放行。</summary>
    internal static string? DuplicateOwner(string commandId, string keys) =>
        App.Settings.Hotkeys
            .Where(b => HotkeyBindingResolver.UnavailableState(b) is null
                        && HotkeyBindingResolver.SameKeys(b.Keys, keys)
                        && !b.Action.Equals(commandId, StringComparison.OrdinalIgnoreCase))
            .Select(b => ActionRegistry.NameOf(b.Action))
            .FirstOrDefault();

    /// <summary>当前绑定是否被外部占用——以 HotkeyService 真实注册结果为准。</summary>
    internal static bool SystemConflict(string commandId)
    {
        var binding = App.Settings.Hotkeys.FirstOrDefault(b => b.Action == commandId);
        return binding is not null && HotkeyService.StateOf(binding) == HotkeyRegState.Conflict;
    }

    /// <summary>捕获结果校验：null = 通过（keys 已就绪可提交）；否则为拒绝原因（裸键/重复绑定）。
    /// 修饰键本身与 Esc/Backspace 不在此列，由调用方先处理。</summary>
    internal static string? Validate(string commandId, Key key, ModifierKeys modifiers, out string keys)
    {
        if (modifiers == ModifierKeys.None && !HotkeyService.AllowedWithoutModifier(key))
        {
            keys = "";
            return "需加 Ctrl/Alt/Shift/Win 修饰键（裸键会拦掉打字），请重按…";
        }
        keys = HotkeyService.BuildKeys(modifiers, key);
        var owner = DuplicateOwner(commandId, keys);
        return owner is null ? null : $"与「{owner}」重复，请重按…";
    }

    internal static string FormatKeys(string keys)
    {
        if (keys == "") return "未绑定";
        var segments = keys.Split('+');
        var last = segments[^1];
        return Enum.TryParse<Key>(last, out var key)
            ? string.Join("+", segments[..^1].Append(KeyDisplayName(key)))
            : keys;
    }

    /// <summary>捕获态实时预览：按住什么修饰键就显示什么。</summary>
    internal static string PreviewText(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join("+", parts) + "+…";
    }

    internal static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    internal static string KeyDisplayName(Key key)
    {
        var k = (int)key;
        if (k >= (int)Key.D0 && k <= (int)Key.D9)
            return ((char)('0' + k - (int)Key.D0)).ToString();
        if (k >= (int)Key.NumPad0 && k <= (int)Key.NumPad9)
            return "Num " + (k - (int)Key.NumPad0);
        return key switch
        {
            Key.OemTilde => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemPipe => "\\",
            Key.MediaPlayPause => "Media 播放/暂停",
            Key.MediaNextTrack => "Media 下一曲",
            Key.MediaPreviousTrack => "Media 上一曲",
            Key.MediaStop => "Media 停止",
            Key.VolumeMute => "静音",
            Key.VolumeUp => "音量 +",
            Key.VolumeDown => "音量 −",
            _ => key.ToString(),
        };
    }
}

/// <summary>
/// 热键捕获会话：低级键盘钩子优先（已被其他程序注册的组合只有这里拦得到），
/// WPF PreviewKeyDown 兜底（钩子安装失败时）。钩子线程回调已封送 UI 线程
/// （同步 Invoke 会与 Uninstall 的 Join 死锁，只能 BeginInvoke）。
/// 职责边界：Esc 取消 / Backspace 清除 / 裸修饰键忽略 / 校验提交，全部由调用方处理。
/// 用完必须 Dispose（退钩子），宿主元素卸载路径要兜底。
/// </summary>
internal sealed class HotkeyCapture : IDisposable
{
    private readonly UIElement _fallbackHost;
    private bool _disposed;

    /// <summary>拦截到完整按键（已封送 UI 线程）。</summary>
    public event Action<Key, ModifierKeys>? KeyIntercepted;

    /// <summary>修饰键按住状态变化（已封送 UI 线程；实时预览用）。</summary>
    public event Action<ModifierKeys>? ModifiersChanged;

    public HotkeyCapture(UIElement fallbackHost) => _fallbackHost = fallbackHost;

    public void Start()
    {
        if (_disposed) return;
        KeyboardCaptureHook.KeyIntercepted += OnHookKey;
        KeyboardCaptureHook.ModifiersChanged += OnHookModifiers;
        KeyboardCaptureHook.Install();
        _fallbackHost.PreviewKeyDown += OnFallbackKeyDown;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        KeyboardCaptureHook.KeyIntercepted -= OnHookKey;
        KeyboardCaptureHook.ModifiersChanged -= OnHookModifiers;
        KeyboardCaptureHook.Uninstall();
        _fallbackHost.PreviewKeyDown -= OnFallbackKeyDown;
    }

    private void OnHookKey(Key key, ModifierKeys modifiers) =>
        _fallbackHost.Dispatcher.BeginInvoke(() => KeyIntercepted?.Invoke(key, modifiers));

    private void OnHookModifiers(ModifierKeys modifiers) =>
        _fallbackHost.Dispatcher.BeginInvoke(() => ModifiersChanged?.Invoke(modifiers));

    private void OnFallbackKeyDown(object sender, KeyEventArgs e)
    {
        // 钩子正常工作时 WPF 收不到键（被钩子吞了），这里只是兜底
        e.Handled = true;
        KeyIntercepted?.Invoke(e.Key == Key.System ? e.SystemKey : e.Key, e.KeyboardDevice.Modifiers);
    }
}
