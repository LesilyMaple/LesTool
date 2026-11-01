using System.IO;
using System.Windows;
using System.Windows.Input;
using LesTool.Platform;
using LesTool.Services;
using LesTool.Services.Plugins;
using LesTool.Services.Scripting;
using Serilog;

namespace LesTool;

public partial class App : Application
{
    private readonly ModuleTable _modules = new();
    private RadialTrigger? _radialTrigger;
    private MouseHookService? _mouseHook;
    private RadialWindowController? _radial;
    private PanelWindowController? _panels;

    /// <summary>全局共享设置实例；各处改动就地生效后整份保存，避免多实例互相覆盖。</summary>
    public static AppSettingsData Settings { get; private set; } = new();

    /// <summary>托盘控制器；托盘设置页热更新图标/名称时经此触达。</summary>
    internal static VolumeMixerController? Mixer { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LesTool");
        Directory.CreateDirectory(Path.Combine(dataDir, "logs"));
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(dataDir, "logs", "lestool-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "未处理 UI 异常");
            ToastService.Show("发生错误：相关功能可能受影响，详见日志", 4000);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "未处理非 UI 异常");
        AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
            Log.Debug(args.Exception, "首现异常 {Type}", args.Exception.GetType().Name);

        Log.Information("========== LesTool 启动 ==========");

        Settings = AppSettings.Load();

        // 模块表：初始化按此序、关停逆序。顺序契约——
        // 主题须先于任何窗口创建；轮盘/面板先于命令注册（回调闭包引用其实例）；
        // 命令注册先于脚本扫描（脚本命令与内置共用注册表）与热键恢复（要能解析命令名）；
        // 录屏/长截图的关停钩子由本表承接（P2 随截图插件迁出宿主）。
        _modules.Add("theme", ThemeManager.Initialize); // 读设置应用主题、挂系统主题变化监听（须在创建窗口前）
        _modules.Add("window", InitializeMainWindow);
        _modules.Add("radial", InitializeRadial);
        _modules.Add("panel", InitializePanel);
        _modules.Add("mouse", InitializeMouseHook, () => _mouseHook?.Dispose());
        _modules.Add("clipboard", ClipboardService.Start, ClipboardService.Shutdown); // 剪贴板元数据监听（新鲜度/来源/排除标记；不存内容）
        _modules.Add("hotkey", HotkeyService.Start);
        _modules.Add("commands", RegisterActions);
        _modules.Add("scripts", ScriptEngine.Initialize); // 用户脚本命令（script: 前缀）；失败只记日志不影响主流程
        _modules.Add("plugins", PluginHost.Load, PluginHost.ShutdownAll); // 外部插件（plugins/*/plugin.json）；单插件失败只 Toast（截图插件装载 screenshot.region 命令并自带编码器预热）
        _modules.Add("mixer", InitializeMixer, () => Mixer?.Dispose()); // 托盘常驻：左键执行配置命令（默认混音器，走命令注册表）
        _modules.InitializeAll();

        // 命令与绑定的收尾：先有命令，再恢复持久化热键；注册失败（被占用）的由页面如实标警告
        HotkeyService.ActionTriggered += id => ActionRegistry.Execute(id);
        EnsureLauncherHotkey();
        HotkeyService.ApplyAll(Settings.Hotkeys);
        WarnHotkeyConflicts();
    }

    private void InitializeMainWindow()
    {
        var window = new MainWindow();
        MainWindow = window;
        // 隐藏的轮盘窗口仍算"打开"，OnLastWindowClose 不会触发，这里显式退出
        window.Closed += (_, _) => Shutdown();
        window.Show();
        Log.Information("主窗口已显示");
    }

    /// <summary>轮盘：右键按住拖动超过 5px 显示，松开隐藏。</summary>
    private void InitializeRadial()
    {
        _radialTrigger = new RadialTrigger();
        _radial = new RadialWindowController();
        _radial.Initialize();
        var trigger = _radialTrigger;
        var radial = _radial;
        trigger.Triggered += radial.ShowAt;
        trigger.Released += radial.Hide;
    }

    private void InitializePanel()
    {
        // 命令面板：热键/命令触发的 uTools 式启动器壳（与轮盘互不干涉，见控制器）
        _panels = new PanelWindowController();
        _panels.Initialize();
    }

    private void InitializeMouseHook()
    {
        if (_radialTrigger is null)
            throw new InvalidOperationException("鼠标钩子依赖轮盘触发器（模块表顺序错误）");
        _mouseHook = new MouseHookService(_radialTrigger);
        _mouseHook.Start();
    }

    private void InitializeMixer()
    {
        Mixer = new VolumeMixerController();
        Mixer.Initialize();
    }

    private void RegisterActions()
    {
        // 顺序契约显式断言：闭包引用运行期实例，实例未就绪=模块表顺序错误，启动期暴露
        var radial = _radial ?? throw new InvalidOperationException("命令注册要求轮盘模块先就绪（模块表顺序错误）");
        var panels = _panels ?? throw new InvalidOperationException("命令注册要求面板模块先就绪（模块表顺序错误）");

        // 截图（screenshot.region）已随截图插件注册（plugins 模块），宿主不再登记

        ActionRegistry.Register("radial.toggle", "呼出轮盘", "\uE700", ctx =>
        {
            if (radial.IsVisible)
            {
                radial.Hide();
            }
            else
            {
                radial.ShowAt(ctx.Cursor);
            }
        }, keywords: "轮盘 手势 radial", requires: ContextKind.Cursor);

        ActionRegistry.Register("panel.launcher", "命令面板", "\uE721", _ => panels.Toggle(),
            keywords: "面板 启动 命令 launcher");

        // 音量混音器（volume.mixer）已随音量插件注册（plugins 模块）；
        // 托盘默认命令指向它，插件缺席时托盘左键回退命令面板（见 VolumeMixerController）
    }

    /// <summary>用户拍板启动器默认 Alt+Space；已有该动作的绑定时不动（想换键在热键页改）。</summary>
    private void EnsureLauncherHotkey()
    {
        if (Settings.Hotkeys.Any(h => h.Action == "panel.launcher")) return;
        Settings.Hotkeys.Add(new HotkeyBinding
        {
            Keys = HotkeyService.BuildKeys(ModifierKeys.Alt, Key.Space),
            Action = "panel.launcher",
            Enabled = true,
        });
        AppSettings.Save(Settings);
        Log.Information("已写入默认热键 {Keys} → {Action}", Settings.Hotkeys[^1].Keys, "panel.launcher");
    }

    /// <summary>注册失败原本只躺在日志与热键页里，用户按键无反应才被发现——启动 Toast 自报冲突。</summary>
    private void WarnHotkeyConflicts()
    {
        var conflicts = Settings.Hotkeys
            .Where(h => h.Enabled && HotkeyService.StateOf(h) == HotkeyRegState.Conflict)
            .Select(h => $"{h.Keys}（{ActionRegistry.NameOf(h.Action)}）")
            .ToList();
        if (conflicts.Count == 0) return;
        Log.Warning("启动热键冲突：{Conflicts}", string.Join("、", conflicts));
        ToastService.Show($"热键被其他程序占用：{string.Join("、", conflicts)}", 4000);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 逆序关停：混音器托盘（收图标防幽灵）→ 长截图/录屏（录制中退出优雅停 ffmpeg、保留临时段）→ … → 鼠标钩子
        _modules.ShutdownAll();
        Log.Information("========== LesTool 退出 ==========");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
