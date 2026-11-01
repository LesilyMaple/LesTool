using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;
using LesTool.Services;
using Serilog;

namespace LesTool.Services;

/// <summary>
/// 托盘常驻（平台服务，应用身份）：左键托盘图标执行配置的命令（默认音量混音器，
/// 经全局命令注册表派发，托盘页可选任意命令）；右键菜单 = 打开主窗口 / 退出。
/// 图标与悬停名称由设置页热配置。混音器本体在音量插件（LesTool.Plugin.Volume）。
/// </summary>
public sealed class VolumeMixerController : IDisposable
{
    private TaskbarIcon? _tray;

    /// <summary>须在 UI 线程调用（托盘图标挂主 UI 线程）。</summary>
    public void Initialize()
    {
        _tray = new TaskbarIcon
        {
            ContextMenu = BuildMenu(),
        };
        ApplyTrayVisual();
        _tray.TrayLeftMouseDown += (_, _) => OnTrayLeftClick();
        Log.Information("托盘图标已创建（左键={Action}）", App.Settings.Tray.LeftClickAction);
    }

    /// <summary>左键走全局命令注册表（TraySettings.LeftClickAction）。
    /// 回退链：配置命令 → volume.mixer → panel.launcher（永远存在）——插件缺席时托盘仍可用。</summary>
    private static void OnTrayLeftClick()
    {
        var id = App.Settings.Tray.LeftClickAction;
        if (ActionRegistry.Find(id) is null)
        {
            var fallback = ActionRegistry.Find("volume.mixer") is not null ? "volume.mixer" : "panel.launcher";
            Log.Warning("托盘左键命令不存在，回退：{Id} → {Fallback}", id, fallback);
            id = fallback;
        }
        ActionRegistry.Execute(id);
    }

    /// <summary>按当前设置重绘图标 + 名称（设置页切换图标、启动时各调一次）。</summary>
    public void ApplyTrayVisual()
    {
        if (_tray is null) return;
        var tray = App.Settings.Tray;
        _tray.ToolTipText = tray.Name;
        _tray.IconSource = TrayIconRenderer.Create(tray.Icon, tray.IconFile, TrayIconRenderer.IsTaskbarLight());
        Log.Information("托盘外观已应用：Icon={Icon}，Name={Name}", tray.Icon, tray.Name);
    }

    /// <summary>只更新悬停名称（名称输入过程中高频调用，不重绘图标不打 INFO）。</summary>
    public void UpdateTooltip()
    {
        if (_tray is null) return;
        _tray.ToolTipText = App.Settings.Tray.Name;
    }

    private static ContextMenu BuildMenu()
    {
        var menu = new ContextMenu { Style = (Style)Application.Current.FindResource("TrayMenuStyle") };

        var open = new MenuItem
        {
            Header = "打开主窗口",
            Style = (Style)Application.Current.FindResource("TrayMenuItemStyle"),
        };
        open.Click += (_, _) =>
        {
            var main = Application.Current.MainWindow;
            if (main is null) return;
            if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
            main.Show();
            main.Activate();
            Log.Debug("托盘菜单：打开主窗口");
        };

        var exit = new MenuItem
        {
            Header = "退出",
            Style = (Style)Application.Current.FindResource("TrayMenuItemStyle"),
        };
        exit.Click += (_, _) =>
        {
            Log.Information("托盘菜单：退出");
            Application.Current.Shutdown();
        };

        menu.Items.Add(open);
        menu.Items.Add(new Separator { Style = (Style)Application.Current.FindResource("TrayMenuSeparatorStyle") });
        menu.Items.Add(exit);
        return menu;
    }

    public void Dispose()
    {
        _tray?.Dispose(); // 先收托盘图标，避免幽灵图标
        Log.Information("托盘已释放");
    }
}
