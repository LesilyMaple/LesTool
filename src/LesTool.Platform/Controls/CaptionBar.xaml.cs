using System.Windows;
using System.Windows.Controls;
using Serilog;

namespace LesTool.Platform.Controls;

public partial class CaptionBar : UserControl
{
    private Window? _host;

    public CaptionBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_host != null) return;
        _host = Window.GetWindow(this);
        if (_host == null) return;
        _host.StateChanged += (_, _) => UpdateMaxIcon();
        UpdateMaxIcon();
        Log.Debug("标题栏已挂接窗口：{Title}", _host.Title);
    }

    private void UpdateMaxIcon()
    {
        if (_host == null) return;
        var maximized = _host.WindowState == WindowState.Maximized;
        IconMax.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        IconRestore.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
        _host?.Dispatcher.Invoke(() => _host.WindowState = WindowState.Minimized);

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        _host?.Dispatcher.Invoke(() =>
            _host.WindowState = _host.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized);

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Log.Debug("标题栏关闭按钮：{Title}", _host?.Title);
        _host?.Close();
    }
}
