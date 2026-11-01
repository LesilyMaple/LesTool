using System.Windows;
using System.Windows.Controls;
using LesTool.Services;

namespace LesTool.Views.Pages;

public partial class SettingsPage : UserControl
{
    private bool _initializing;

    public SettingsPage()
    {
        InitializeComponent();
        _initializing = true; // 初始勾选只反映现状，不触发重复保存
        (ThemeManager.Mode switch
        {
            ThemeMode.Light => LightOption,
            ThemeMode.System => SystemOption,
            _ => DarkOption,
        }).IsChecked = true;
        _initializing = false;
    }

    private void OnThemeChecked(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var mode = Enum.Parse<ThemeMode>((string)((RadioButton)sender).Tag);
        ThemeManager.SetMode(mode);
    }
}
