using System.Runtime.InteropServices;
using Serilog;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace LesTool.Services;

public enum ThemeMode { Dark, Light, System }

public static class ThemeManager
{
    private const int DwmwaUseImmersiveDarkMode = 20; // build 18985+
    private const int DwmwaUseImmersiveDarkModeLegacy = 19; // 更老的 Win10

    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpFramechanged = 0x0020;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    public static ThemeMode Mode { get; private set; } = ThemeMode.Dark;

    /// <summary>当前实际呈现是否为浅色（System 时取系统应用主题）。</summary>
    public static bool IsLightEffective => IsLight(Mode);

    private static bool IsLight(ThemeMode mode)
    {
        if (mode == ThemeMode.Dark) return false;
        if (mode == ThemeMode.Light) return true;
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v ? v != 0 : true;
    }

    /// <summary>启动时调用：载入持久化主题并挂系统主题变化监听。须在 Log 配置之后、创建窗口之前。</summary>
    public static void Initialize()
    {
        Mode = AppSettings.ParseTheme(App.Settings.Theme);
        Log.Information("主题初始化：{Mode}，实际呈现 {Effective}", Mode, IsLight(Mode) ? "浅色" : "深色");
        Apply();
        // 任一窗口（含以后新增）加载即自动同步标题栏深浅，各窗口无需自行调用；
        // SourceInitialized 是普通 CLR 事件挂不了类处理器，Loaded 时句柄同样已就绪
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ApplyCaptionTheme((Window)sender)));
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    public static void SetMode(ThemeMode mode)
    {
        Log.Information("主题切换：{Old} → {New}", Mode, mode);
        Mode = mode;
        App.Settings.Theme = mode.ToString(); // 与热键等共用同一份设置实例，整份保存
        AppSettings.Save(App.Settings);
        Apply();
    }

    private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Win10 切换系统深浅主题以此类别广播；仅跟随系统模式需要响应
        if (e.Category == UserPreferenceCategory.General && Mode == ThemeMode.System)
        {
            Log.Information("系统深浅主题变化，跟随系统重算（现呈现 {Effective}）", IsLight(Mode) ? "浅色" : "深色");
            Application.Current.Dispatcher.Invoke(Apply);
        }
    }

    private static void Apply()
    {
        var light = IsLight(Mode);
        var source = new Uri(
            $"pack://application:,,,/Themes/{(light ? "Light" : "Dark")}.xaml", UriKind.Absolute);
        // 只替换主题字典（Design.xaml 等常驻字典保留），且必须 Add 到末尾：合并字典后加者优先
        var dicts = Application.Current.Resources.MergedDictionaries;
        for (var i = dicts.Count - 1; i >= 0; i--)
        {
            var path = dicts[i].Source?.OriginalString;
            if (path != null && (path.Contains("Dark.xaml") || path.Contains("Light.xaml")))
                dicts.RemoveAt(i);
        }
        dicts.Add(new ResourceDictionary { Source = source });

        foreach (Window window in Application.Current.Windows)
            ApplyCaptionTheme(window);
    }

    /// <summary>窗口句柄就绪后调用，让系统标题栏随主题变黑/白。</summary>
    public static void ApplyCaptionTheme(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsLightEffective ? 0 : 1;
        var hr = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        if (hr != 0)
            hr = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
        if (hr != 0)
        {
            Log.Debug("标题栏深浅设置失败：hwnd=0x{Hwnd}，hr=0x{Hr:X8}", hwnd, hr);
            return;
        }
        // 部分 Win10 build 收到新属性后不主动重绘标题栏，强制刷一次非客户区
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNosize | SwpNomove | SwpNozorder | SwpFramechanged);
    }
}
