using System.Windows;
using System.Windows.Threading;
using LesTool.Platform;
using LesTool.Views;
using Serilog;

namespace LesTool.Services;

/// <summary>
/// 轮盘会话控制器：启动时预创建隐藏窗口（HWND 与渲染路径已就绪），
/// 触发时只做配置 + 定位 + Show，把首次显示的开销全部移到启动期。
/// 会话生命周期（全部 UI 线程执行；输入事件来自钩子线程，异步封送进来）：
/// 显示（装 Esc 钩子 + 起超时定时器）→ 悬停更新 → 终结（松开执行/左键取消/
/// Esc 取消/超时收起四条出口统一走 EndSession）。
/// 自愈：窗口被外部关闭（Alt+F4 / 反作弊清理覆盖层等）不再永久罢工——
/// 触发时发现窗口不在或显示失败即重建，并留日志记录被关时间。
/// </summary>
public sealed class RadialWindowController
{
    private RadialWindow? _window;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _timeout;
    private int _generation;
    private TriggerSnapshot? _snapshot;

    /// <summary>须在 UI 线程调用。</summary>
    public void Initialize()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        CreateWindow();
        // 钩子线程回调：只做异步封送（同步 Invoke 会与 Uninstall 的 Join 死锁）
        RadialEscapeHook.EscPressed += () => RunOnUi(() => Cancel("Esc"));
    }

    /// <summary>线程安全：钩子线程可直接调用，内部封送到 UI 线程。</summary>
    public void ShowAt(Point centerScreenPx)
    {
        RunOnUi(() =>
        {
            var window = _window ?? CreateWindow();
            try
            {
                var radial = App.Settings.Radial;
                radial.Normalize();
                var scene = RadialSceneResolver.ForegroundScene(); // 全局默认 + 应用覆盖合成
                var target = WindowTarget.Capture();
                _snapshot = new TriggerSnapshot(target.Handle) { Target = target };
                var generation = ++_generation;
                var effective = RadialSceneResolver.EffectiveActions(scene);
                var initial = effective.Select(id => IsSelectionGated(id) ? null : id).ToList();
                window.ApplySize(radial.SizeDip); // 须先于 Configure：扇区按当前尺寸绘制，否则首显用旧比例画（2026-10-17 首次唤出 UI 错位的根因）
                window.Configure(initial);
                window.ShowAt(centerScreenPx);
                if (effective.Any(IsSelectionGated)) _ = UpdateSelectionGateAsync(window, effective, target, generation);

                _timeout ??= CreateTimeoutTimer();
                _timeout.Interval = TimeSpan.FromSeconds(radial.TimeoutSeconds);
                _timeout.Stop();
                _timeout.Start();
                RadialEscapeHook.Install();

                Log.Information("轮盘已显示（场景 {Scene}，{Configured}/8 槽生效，超时 {Seconds}s）",
                    scene?.Process ?? "全局", effective.Count(a => a != null), radial.TimeoutSeconds);
            }
            catch (Exception ex)
            {
                // 窗口处于已关闭状态等：重建一次并重试，避免整段会话轮盘永久失效
                Log.Error(ex, "轮盘显示失败，重建窗口后重试");
                window = CreateWindow();
                window.ShowAt(centerScreenPx);
                Log.Information("轮盘窗口已重建并显示");
            }
        });
    }

    /// <summary>上下文门禁（2026-10-29 拍板）：Requires 含 SelectedText 的命令，
    /// 轮盘展示前用 UIA 只问不拿地探测——前台没有选中文字就把该槽置空（空槽视觉，
    /// 布局不挪动，有选中即恢复）。绑定清单（Bindable）不受影响，否则没法绑进槽位。
    /// 当前生效槽位里没有此类命令时零成本，不探测。</summary>
    private static bool IsSelectionGated(string? id) => id is not null
        && ActionRegistry.Find(id)?.Requires.HasFlag(ContextKind.SelectedText) == true;

    private async Task UpdateSelectionGateAsync(RadialWindow window, List<string?> slots, WindowTarget target, int generation)
    {
        try
        {
            if (await SelectionText.ProbeSelectionAsync(target) && generation == _generation
                && ReferenceEquals(window, _window) && window.IsVisible && target.IsForeground) window.Configure(slots);
        }
        catch (Exception error) { Log.Debug(error, "轮盘选区探测失败"); }
    }

    /// <summary>悬停更新（钩子线程每帧移动事件；窗口不可见时丢弃）。</summary>
    public void UpdateHover(Point screenPx) =>
        RunOnUi(() =>
        {
            if (_window?.IsVisible == true)
                _window.UpdateHover(screenPx);
        });

    /// <summary>松开右键：悬停命令存在则派发（走命令注册表，与其他触发源同一通道），否则收起。</summary>
    public void Release() =>
        RunOnUi(() =>
        {
            if (_window?.IsVisible != true) return;

            var command = _window.SelectedCommand;
            EndSession();
            if (command is null)
            {
                Log.Debug("轮盘收起：未选中命令");
                return;
            }

            Log.Information("轮盘执行：{Command}（{Name}）", command, ActionRegistry.NameOf(command));
            ActionRegistry.Execute(command, TriggerSource.Radial, _snapshot);
        });

    /// <summary>左键取消（Quicker 同款：显示期间点左键 = 取消且不点穿）。</summary>
    public void CancelByLeftClick() => RunOnUi(() => Cancel("左键"));

    /// <summary>轮盘当前是否可见（快捷键呼出做开关用；UI 线程查询）。</summary>
    public bool IsVisible => _window?.IsVisible ?? false;

    private void Cancel(string reason)
    {
        if (_window?.IsVisible != true) return;
        Log.Information("轮盘取消：{Reason}", reason);
        EndSession();
    }

    /// <summary>会话收尾（UI 线程）：停定时器、卸 Esc 钩子、藏窗口。四条出口共用。</summary>
    private void EndSession()
    {
        _generation++;
        _timeout?.Stop();
        RadialEscapeHook.Uninstall();
        try
        {
            _window?.Hide();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "轮盘隐藏失败，丢弃旧窗口待重建");
            _window = null;
        }
    }

    private DispatcherTimer CreateTimeoutTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background);
        timer.Tick += (_, _) => Cancel("超时未选择");
        return timer;
    }

    private void RunOnUi(Action action)
    {
        var dispatcher = _dispatcher ??= Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(action);
    }

    /// <summary>在 UI 线程创建并预热窗口；窗口被外部关闭时置空、下次触发重建。</summary>
    private RadialWindow CreateWindow()
    {
        var window = new RadialWindow();
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_window, window)) return;
            _window = null;
            Log.Information("轮盘窗口被外部关闭（将在下次触发时自动重建）");
            // 会话中途被关也要收尾：否则 Esc 钩子会一直白吞 Esc、定时器空转，直到下次手势
            _timeout?.Stop();
            RadialEscapeHook.Uninstall();
        };
        window.PrepareForFastShow();
        _window = window;
        return window;
    }
}
