using System.Runtime.InteropServices;
using System.Windows.Interop;
using Serilog;

namespace LesTool.Services;

/// <summary>
/// 全局热键：RegisterHotKey 绑定到 UI 线程的 message-only 窗口（HWND_MESSAGE），
/// WM_HOTKEY 经 Dispatcher 消息泵送达。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4C54;

    private readonly SettingsService _settings;
    private HwndSource? _source;
    private bool _registered;

    public event Action? HotkeyPressed;

    public HotkeyService(SettingsService settings) => _settings = settings;

    /// <summary>须在 UI 线程调用。</summary>
    public void Start()
    {
        var parameters = new HwndSourceParameters("LesTool.MessageWindow")
        {
            ParentWindow = (IntPtr)(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        var (modifiers, vk) = ParseHotkey(_settings.Current.Hotkey);
        if (vk == 0)
        {
            Log.Warning("热键配置 {Hotkey} 无法解析", _settings.Current.Hotkey);
            return;
        }

        if (RegisterHotKey(_source.Handle, HotkeyId, modifiers, vk))
        {
            _registered = true;
            Log.Information("热键 {Hotkey} 已注册", _settings.Current.Hotkey);
        }
        else
        {
            Log.Warning("热键 {Hotkey} 注册失败（错误码 {ErrorCode}），可能已被其他程序占用",
                _settings.Current.Hotkey, Marshal.GetLastWin32Error());
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>"Ctrl+Alt+P" → (修饰键掩码, 虚拟键码)。支持 Ctrl/Alt/Shift/Win + 字母/数字/F1–F12。</summary>
    private static (uint Modifiers, uint Vk) ParseHotkey(string text)
    {
        uint modifiers = 0, vk = 0;
        foreach (var token in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= 0x0002; // MOD_CONTROL
                    break;
                case "alt":
                    modifiers |= 0x0001; // MOD_ALT
                    break;
                case "shift":
                    modifiers |= 0x0004; // MOD_SHIFT
                    break;
                case "win" or "meta":
                    modifiers |= 0x0008; // MOD_WIN
                    break;
                default:
                    if (token.Length == 1 && char.IsLetterOrDigit(token[0]))
                        vk = char.ToUpperInvariant(token[0]);
                    else if (token.Length > 1 && token[0] is 'f' or 'F'
                             && int.TryParse(token[1..], out var f) && f is >= 1 and <= 12)
                        vk = 0x70 + (uint)(f - 1);
                    break;
            }
        }
        return (modifiers, vk);
    }

    public void Dispose()
    {
        if (_registered && _source is not null && !_source.IsDisposed)
            UnregisterHotKey(_source.Handle, HotkeyId);
        _source?.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
