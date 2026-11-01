using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LesTool.Platform;
using LesTool.Services;
using LesTool.Services.Plugins;
using Serilog;

namespace LesTool;

public partial class App : Application
{
    private readonly ModuleTable _modules = new();
    private readonly ShutdownCoordinator _shutdown = new();
    private bool _closeReady;
    private RadialTrigger? _radialTrigger;
    private MouseHookService? _mouseHook;
    private RadialWindowController? _radial;
    private PanelWindowController? _panels;

    /// <summary>全局共享设置实例；各处改动就地生效后整份保存，避免多实例互相覆盖。</summary>
    public static AppSettingsData Settings { get; private set; } = new();
    internal static void ReplaceSettings(AppSettingsData value) => Settings = value;

    /// <summary>Shift+滚轮 → 横向平滑滚动（每格 48px，渲染帧指数趋近）。
    /// Windows/WPF 均不自带此约定（浏览器是自己实现的），预览阶段拦截并标记已处理，
    /// 避免同一滚动再触发默认竖向；无横向余量时不拦截，行为回落原生。</summary>
    private static void OnScrollViewerShiftWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Shift)
            return;
        var viewer = (ScrollViewer)sender;
        if (viewer.ScrollableWidth <= 0)
            return;
        SmoothHorizontalScroll.Kick(viewer, -e.Delta / 120.0 * 48);
        Log.Debug("Shift+滚轮横向平滑滚动（动画细节见引擎日志）");
        e.Handled = true;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 3 && e.Args[0] == "--diagnose-plugins")
        {
            try { Shutdown(PluginDiagnostics.Run(e.Args[1], e.Args[2])); }
            catch { Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-plugin-startup")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = VerifyPluginStartupAsync(e.Args[1]);
            return;
        }
        base.OnStartup(e);

        // 单实例闸先于一切：钩子/热键不能注册两份；日志文件被首实例独占，此时退出静默无痕
        if (!SingleInstanceGuard.TryAcquire())
        {
            Shutdown();
            return;
        }

        // 设置先于日志加载：日志自动清理周期要读它。此行之前无 Logger，Load 内部的日志走静默实例被丢弃
        Settings = AppSettings.Load();
        LogRetentionManager.Configure();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "未处理 UI 异常");
            ToastService.Show(args.Exception is LesTool.Platform.Storage.SettingsWriteException
                ? "设置未保存，原文件已保留；请查看设置或插件管理页"
                : "发生错误：相关功能可能受影响，详见日志", 4000);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "未处理非 UI 异常");
        AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
            Log.Debug(args.Exception, "首现异常 {Type}", args.Exception.GetType().Name);

        // 类级注册：应用内全部 ScrollViewer（含模板内嵌与插件窗口）一次接线
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ScrollViewer),
            Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnScrollViewerShiftWheel));

        Log.Information("========== LesTool 启动 ==========");

        // 模块表：初始化按此序、关停逆序。顺序契约——
        // 主题须先于任何窗口创建；轮盘/面板先于命令注册（回调闭包引用其实例）；
        // 命令注册先于热键恢复（要能解析命令名）；
        // 录屏/长截图的关停钩子由本表承接（P2 随截图插件迁出宿主）。
        _modules.Add("theme", ThemeManager.Initialize, ThemeManager.Shutdown); // 读设置应用主题、挂系统主题变化监听（须在创建窗口前）
        _modules.Add("window", InitializeMainWindow);
        _modules.Add("radial", InitializeRadial);
        _modules.Add("panel", InitializePanel);
        _modules.Add("mouse", InitializeMouseHook, () => _mouseHook?.Dispose());
        _modules.Add("clipboard", ClipboardService.Start, ClipboardService.Shutdown); // 剪贴板元数据监听（新鲜度/来源/排除标记；不存内容）
        _modules.Add("hotkey", HotkeyService.Start, HotkeyService.Stop);
        _modules.Add("commands", RegisterActions);
        _modules.Add("plugins", PluginHost.Load, PluginHost.ShutdownAll); // 外部插件（plugins/*/plugin.json）；单插件失败只 Toast（截图插件装载 screenshot.region 命令并自带编码器预热）
        try { _modules.InitializeAll(); }
        catch (Exception error)
        {
            Log.Fatal(error, "启动模块失败，回收并退出");
            _modules.ShutdownAll();
            Shutdown(1);
            return;
        }

        // 命令与绑定的收尾：先有命令，再恢复持久化热键；注册失败（被占用）的由页面如实标警告
        HotkeyService.ActionTriggered += id => ActionRegistry.Execute(id, TriggerSource.Hotkey);
        EnsureLauncherHotkey();
        HotkeyService.ApplyAll(Settings.Hotkeys);
        WarnHotkeyConflicts();
        if (AppSettings.IsProtected || PluginState.IsProtected)
            ToastService.Show("部分配置需要恢复，请查看设置或插件管理页；原文件已保留", 5000);
    }

    private async Task VerifyPluginStartupAsync(string reportPath)
    {
        var code = await PluginStartupCheck.RunAsync(reportPath);
        Shutdown(code);
    }

    private void InitializeMainWindow()
    {
        var window = new MainWindow();
        MainWindow = window;
        // 隐藏的轮盘窗口仍算"打开"，OnLastWindowClose 不会触发，这里显式退出
        window.Closing += async (_, args) =>
        {
            if (_closeReady) return;
            args.Cancel = true;
            try { await _shutdown.StopAsync(_modules.ShutdownAll); }
            catch (Exception error) { Log.Error(error, "应用关停失败"); }
            _closeReady = true;
            Shutdown();
        };
        window.Show();
        Log.Information("主窗口已显示");
    }

    /// <summary>轮盘：右键按住拖动超 5px 显示；划到扇区松开执行命令，划出/空槽松开、
    /// Esc、左键或超时收起。场景：全局默认 + 按前台应用覆盖，禁用的软件里退化为普通右键。</summary>
    private void InitializeRadial()
    {
        _radialTrigger = new RadialTrigger();
        _radial = new RadialWindowController();
        _radial.Initialize();
        var trigger = _radialTrigger;
        var radial = _radial;
        trigger.Triggered += radial.ShowAt;
        trigger.Moved += radial.UpdateHover;     // 显示期移动 → 扇区悬停高亮
        trigger.Released += radial.Release;      // 松开 → 执行选中命令或收起
        trigger.SuppressTrigger = RadialSceneResolver.IsForegroundDisabled; // 钩子线程同步闸：只读 volatile 快照
        RadialSceneResolver.RefreshSnapshot();
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
        if (_radial is not null)
            _mouseHook.RadialLeftClickCancel += _radial.CancelByLeftClick; // 显示期左键 = 取消；轮盘模块在鼠标模块之前初始化（模块表顺序），此处非空
        _mouseHook.Start();
    }

    private void RegisterActions()
    {
        // 顺序契约显式断言：闭包引用运行期实例，实例未就绪=模块表顺序错误，启动期暴露
        var panels = _panels ?? throw new InvalidOperationException("命令注册要求面板模块先就绪（模块表顺序错误）");

        // 内置壳命令（2026-10-31 起用场景声明表达，替代 Kind 一刀切）：面板只经快捷键触发
        // ——不进命令面板清单（面板呼面板是套娃）、不进轮盘（鼠标流不放键盘入口），
        // 但可在热键区重复绑定（键位归「命令面板」设置页改）。
        // 截图（screenshot.region）等其余命令已随各自插件注册（plugins 模块）。
        ActionRegistry.Register("panel.launcher", "命令面板", "\uE721", _ => panels.Toggle(),
            keywords: "面板 启动 命令 launcher",
            scenarios: CommandScenarios.Hotkey);
    }

    /// <summary>用户拍板启动器默认 Alt+Space；已有该动作的绑定时不动（想换键在「命令面板」页改）。</summary>
    private void EnsureLauncherHotkey()
    {
        if (!LauncherHotkeyDefaults.InitializeOnce(Settings)) return;
        AppSettings.Save(Settings);
        Log.Information("命令面板默认键位初始化完成");
    }

    /// <summary>注册失败原本只躺在日志与各插件页的热键区里，用户按键无反应才被发现——启动 Toast 自报冲突。</summary>
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
        // 逆序关停：长截图/录屏（录制中退出优雅停 ffmpeg、保留临时段）→ … → 鼠标钩子
        _modules.ShutdownAll();
        Log.Information("========== LesTool 退出 ==========");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}

/// <summary>横向平滑滚动引擎：滚格累计到目标偏移，按渲染帧指数趋近（时间常数 90ms，
/// 一格 48px 约四分之三秒内收敛，连滚自然续滑）。全局只在有动画时挂一个渲染回调，
/// 队列空即摘除，平时零开销；外部改偏移（拖滑块/布局变化）立即让位中止。
/// 状态表用 ConditionalWeakTable，随控件回收自动释放。</summary>
internal static class SmoothHorizontalScroll
{
    private const double TimeConstantMs = 90; // 趋近时间常数：顺滑不拖沓的平衡点
    private const double Epsilon = 0.5;       // 收敛阈值（px），低于即贴齐收车

    private sealed class State
    {
        public double Current; // 引擎视角的当前偏移；与控件实际值背离即外部干预
        public double Target;  // 目标偏移，滚格连续累计
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();
    private static readonly HashSet<ScrollViewer> Active = new();
    private static bool _hooked;
    private static TimeSpan _lastFrame;

    /// <summary>踢一脚：加一段目标偏移并确保动画在跑。delta 正=向右。</summary>
    public static void Kick(ScrollViewer viewer, double delta)
    {
        var state = States.GetOrCreateValue(viewer);
        lock (Active)
        {
            if (!Active.Contains(viewer))
            {
                state.Current = viewer.HorizontalOffset;
                state.Target = viewer.HorizontalOffset + delta;
            }
            else
            {
                state.Target += delta;
            }
            state.Target = Math.Clamp(state.Target, 0, viewer.ScrollableWidth);
            Active.Add(viewer);
            if (!_hooked)
            {
                CompositionTarget.Rendering += OnRendering;
                _hooked = true;
            }
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var frame = ((RenderingEventArgs)e).RenderingTime;
        // 帧间隔夹在 1~50ms：首帧无基准取 60fps 值，掉帧不跳变
        double dt = _lastFrame == default ? 16.7 : Math.Clamp((frame - _lastFrame).TotalMilliseconds, 1, 50);
        _lastFrame = frame;
        double factor = 1 - Math.Exp(-dt / TimeConstantMs);

        List<ScrollViewer> finished = new();
        lock (Active)
        {
            foreach (ScrollViewer viewer in Active.ToList())
            {
                var state = States.GetOrCreateValue(viewer);
                if (Math.Abs(viewer.HorizontalOffset - state.Current) > 1.5)
                {
                    // 外部改了偏移（拖滑块、内容布局变化）：让位，本观众收车
                    state.Current = state.Target = viewer.HorizontalOffset;
                    finished.Add(viewer);
                    continue;
                }
                state.Target = Math.Clamp(state.Target, 0, viewer.ScrollableWidth);
                state.Current += (state.Target - state.Current) * factor;
                if (Math.Abs(state.Target - state.Current) < Epsilon)
                {
                    state.Current = state.Target;
                    finished.Add(viewer);
                    Log.Debug("平滑滚动收敛：{Offset:F0}/{Max:F0}", state.Current, viewer.ScrollableWidth);
                }
                viewer.ScrollToHorizontalOffset(state.Current);
            }
            foreach (ScrollViewer viewer in finished)
                Active.Remove(viewer);
            if (Active.Count == 0 && _hooked)
            {
                CompositionTarget.Rendering -= OnRendering;
                _hooked = false;
            }
        }
    }
}
