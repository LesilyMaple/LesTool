using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;

namespace LesTool.Platform.Controls;

/// <summary>
/// 自说明开关行（PLUGIN_UI_SPEC B）：CheckBox 句式标签 + 可选行下弱色说明。
/// 与 SettingsRow（标签左控件右）二选一：设置项本身就是一句开关语义时用本行。
/// 构造期程序化回填 IsChecked 也会走 Toggled，页面按惯例用 IsLoaded 守卫过滤。
/// </summary>
[ContentProperty(nameof(Text))]
public class CheckRow : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(CheckRow), new PropertyMetadata(""));

    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
        nameof(IsChecked), typeof(bool?), typeof(CheckRow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnIsCheckedChanged));

    public static readonly DependencyProperty HelpProperty = DependencyProperty.Register(
        nameof(Help), typeof(string), typeof(CheckRow), new PropertyMetadata("", OnHelpChanged));

    private readonly CheckBox _box = new();
    private readonly TextBlock _help = new();

    public static readonly RoutedEvent ToggledEvent = EventManager.RegisterRoutedEvent(
        "Toggled", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(CheckRow));

    /// <summary>勾选变化（含绑定/程序化回填触达；构造期回填请用 IsLoaded 守卫）。</summary>
    public event RoutedEventHandler Toggled
    {
        add => AddHandler(ToggledEvent, value);
        remove => RemoveHandler(ToggledEvent, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool? IsChecked
    {
        get => (bool?)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    /// <summary>行下弱色说明（可选）。</summary>
    public string Help
    {
        get => (string)GetValue(HelpProperty);
        set => SetValue(HelpProperty, value);
    }

    public CheckRow()
    {
        Margin = new Thickness(0, 0, 0, 12);

        // 标签强制换行：开关文案常是长句，不许撑破页宽
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Text)) { Source = this });
        _box.Content = label;
        // 注意 DP 要用 CheckBox.IsCheckedProperty（DP 按 owner 注册，传本类的会运行时抛异常）
        _box.SetBinding(CheckBox.IsCheckedProperty,
            new Binding(nameof(IsChecked)) { Source = this, Mode = BindingMode.TwoWay });

        _help.TextWrapping = TextWrapping.Wrap;
        _help.Margin = new Thickness(24, 0, 0, 0);
        _help.Visibility = Visibility.Collapsed;
        _help.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
        _help.SetResourceReference(TextBlock.ForegroundProperty, "TextHint");

        Children.Add(_box);
        Children.Add(_help);
    }

    private static void OnIsCheckedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 从本类 DP 变更回调发事件（内层勾选框/外部绑定/程序化赋值三条路都汇聚到这，
        // 触发时读 IsChecked 必然已是新值）。必须走登记过的路由事件：RoutedEventArgs
        // 没挂 RoutedEvent 时设 Source 直接抛 InvalidOperationException（2026-10-31
        // 三插件页构造期回填即崩，日志实锤）。
        var row = (CheckRow)d;
        row.RaiseEvent(new RoutedEventArgs(ToggledEvent, row));
    }

    private static void OnHelpChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (CheckRow)d;
        row._help.Visibility = string.IsNullOrEmpty(row.Help) ? Visibility.Collapsed : Visibility.Visible;
    }
}
