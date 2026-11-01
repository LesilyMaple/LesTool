using System.Windows;
using System.Windows.Controls;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.ClipboardHistory;

/// <summary>剪切板历史设置页。工厂每次进页重建，设置读写经 IPluginSettings 即时生效。</summary>
public partial class ClipboardSettingsPage : UserControl
{
    private const int ClearArmMs = 3000;

    private readonly IPluginSettings _settings;
    private readonly ClipboardHistory _history;
    private bool _clearArmed;
    private int _clearArmedAt;

    public ClipboardSettingsPage(IPluginSettings settings, ClipboardHistory history)
    {
        _settings = settings;
        _history = history;
        InitializeComponent();
        PasteBackCheck.IsChecked = settings.Get("pasteBack", true);
        ExcludeCheck.IsChecked = settings.Get("excludeMarked", false);
        if (_history.IsProtected) ClearButton.Content = "归档原件并恢复空白历史";
        RefreshStats();
    }

    private void RefreshStats() =>
        MaintenanceSection.Caption =
            _history.IsProtected ? "历史文件无法读取，原件已受保护。可归档原件并恢复空白历史。"
            : $"当前 {_history.Items.Count} 条（其中图片 {_history.ImageCount} 张），不设上限";

    private void OnPasteBackChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // 构造期初值回填不当作用户操作
        var enabled = PasteBackCheck.IsChecked == true;
        _settings.Set("pasteBack", enabled);
        Log.Information("设置变更：回贴{State}", enabled ? "开启" : "关闭");
    }

    private void OnExcludeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var enabled = ExcludeCheck.IsChecked == true;
        _settings.Set("excludeMarked", enabled);
        Log.Information("设置变更：免记录排除{State}", enabled ? "开启" : "关闭");
    }

    /// <summary>清空是不可逆批量删除：两击确认，3 秒未确认自动回常态。</summary>
    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (!_clearArmed || Environment.TickCount - _clearArmedAt > ClearArmMs)
        {
            _clearArmed = true;
            _clearArmedAt = Environment.TickCount;
            ClearButton.Content = _history.IsProtected ? "再次点击确认归档并恢复" : "再点一次确认清空";
            return;
        }
        _clearArmed = false;
        ClearButton.Content = "清空全部历史";
        if (_history.IsProtected) _history.ResetProtectedHistory();
        else { _history.ClearAll(); _history.SaveNow(); }
        RefreshStats();
        ToastService.Show("剪切板历史已清空", 2500);
    }
}
