using System.Windows;
using System.Windows.Controls;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.SmartSearch;

/// <summary>智能搜索设置页：搜索引擎 / 识别开关 / 自定义规则三区。
/// 行内 TextBox 走绑定（LostFocus 提交），失焦或点击即保存并即时生效
/// （引擎与规则表常驻共享，识别管线每次现读）。工厂每次进页重建。</summary>
public partial class SmartSearchSettingsPage : UserControl
{
    private readonly IPluginSettings _settings;
    private readonly EngineStore _store;
    private bool _rebuilding; // 列表重建期绑定初始化会触发 Checked/LostFocus，不当作用户操作

    public SmartSearchSettingsPage(IPluginSettings settings, EngineStore store)
    {
        _settings = settings;
        _store = store;
        InitializeComponent();
        RebuildEngines();
        RebuildRules();
        foreach (var row in RecognizerPanel.Children.OfType<Platform.Controls.CheckRow>())
            row.IsChecked = settings.Get($"recognizer.{(row.Tag as string) ?? ""}", true);
    }

    // ---------- 搜索引擎 ----------

    private void RebuildEngines()
    {
        _rebuilding = true;
        EngineList.ItemsSource = null;
        EngineList.ItemsSource = _store.Engines.ToList();
        _rebuilding = false;
    }

    private void OnEngineEdited(object sender, RoutedEventArgs e)
    {
        if (_rebuilding) return;
        _store.SaveEngines();
    }

    /// <summary>默认引擎单选：全表唯一化后保存。GroupName 只管视觉互斥，标志位在此对齐。</summary>
    private void OnDefaultEngineChecked(object sender, RoutedEventArgs e)
    {
        if (_rebuilding) return;
        if (sender is not RadioButton { DataContext: SearchEngine engine }) return;
        foreach (var item in _store.Engines)
            item.IsDefault = ReferenceEquals(item, engine);
        _store.SaveEngines();
    }

    private void OnAddEngine(object sender, RoutedEventArgs e)
    {
        _store.Engines.Add(new SearchEngine { Name = "新引擎", Url = "https://www.example.com/search?q={0}" });
        _store.SaveEngines();
        RebuildEngines();
    }

    private void OnDeleteEngine(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SearchEngine engine }) return;
        _store.Engines.Remove(engine);
        _store.SaveEngines();
        RebuildEngines();
    }

    // ---------- 识别开关 ----------

    private void OnRecognizerToggle(object sender, RoutedEventArgs e)
    {
        if (_rebuilding || !IsLoaded) return;
        if (sender is Platform.Controls.CheckRow { Tag: string key, IsChecked: { } on })
        {
            _settings.Set($"recognizer.{key}", on);
            Log.Information("设置变更：识别器 {Key} {State}", key, on ? "开启" : "关闭");
        }
    }

    // ---------- 自定义规则 ----------

    private void RebuildRules()
    {
        _rebuilding = true;
        RuleList.ItemsSource = null;
        RuleList.ItemsSource = _store.Rules.ToList();
        _rebuilding = false;
    }

    private void OnRuleEdited(object sender, RoutedEventArgs e)
    {
        if (_rebuilding) return;
        _store.SaveRules();
    }

    private void OnRuleToggled(object sender, RoutedEventArgs e)
    {
        if (_rebuilding || !IsLoaded) return;
        _store.SaveRules();
        if (sender is CheckBox { DataContext: CustomRule rule })
            Log.Information("设置变更：自定义规则 {Name} {State}", rule.Name, rule.Enabled ? "启用" : "停用");
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        _store.Rules.Add(new CustomRule { Name = "新规则", Pattern = @"^正则$", Url = "https://example.com/search?q={0}" });
        _store.SaveRules();
        RebuildRules();
    }

    private void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CustomRule rule }) return;
        _store.Rules.Remove(rule);
        _store.SaveRules();
        RebuildRules();
    }
}
