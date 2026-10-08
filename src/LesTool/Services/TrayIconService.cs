using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;

namespace LesTool.Services;

public sealed class TrayIconService : IDisposable
{
    private TaskbarIcon? _icon;

    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;

    /// <summary>须在 UI 线程调用。</summary>
    public void Initialize()
    {
        var menu = new ContextMenu();
        var settingsItem = new MenuItem { Header = "设置(_S)…", FontWeight = FontWeights.Bold };
        settingsItem.Click += (_, _) => OpenSettingsRequested?.Invoke();
        var exitItem = new MenuItem { Header = "退出(_X)" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ToolTipText = "LesTool",
            ContextMenu = menu,
        };
        _icon.TrayLeftMouseUp += (_, _) => OpenSettingsRequested?.Invoke();
    }

    public void Dispose() => _icon?.Dispose();
}
