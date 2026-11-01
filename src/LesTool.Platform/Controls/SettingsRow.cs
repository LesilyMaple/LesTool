using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;

namespace LesTool.Platform.Controls;

/// <summary>
/// 表单行（PLUGIN_UI_SPEC B）：标签列固定 120（全页竖向对齐）+ 控件区（16 间隔），
/// 一行只放一个设置项。行下可挂 Help（弱色说明）与 Error（危险色校验错误，
/// 行下红字不弹窗；非空时顶替 Help 显示）。默认行距 12（底边距）。
/// </summary>
[ContentProperty(nameof(Content))]
public class SettingsRow : StackPanel
{
    /// <summary>标签列固定宽：五字标签 + 余量，全页所有行共用同一列线。</summary>
    public const double LabelColumn = 120;

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SettingsRow), new PropertyMetadata(""));

    public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(
        nameof(Content), typeof(object), typeof(SettingsRow), new PropertyMetadata(null));

    public static readonly DependencyProperty HelpProperty = DependencyProperty.Register(
        nameof(Help), typeof(string), typeof(SettingsRow),
        new PropertyMetadata("", OnHelpChanged));

    public static readonly DependencyProperty ErrorProperty = DependencyProperty.Register(
        nameof(Error), typeof(string), typeof(SettingsRow), new PropertyMetadata("", OnAssistChanged));

    private readonly TextBlock _label = new();
    private readonly ContentPresenter _host = new();
    private readonly TextBlock _help = new();
    private readonly TextBlock _error = new();

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>控件区内容（一行一控件：TextBox / 单选组 / 按钮组等）。</summary>
    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    /// <summary>行下弱色说明（可选）。</summary>
    public string Help
    {
        get => (string)GetValue(HelpProperty);
        set => SetValue(HelpProperty, value);
    }

    /// <summary>行下校验错误（可选，危险色）。非空时顶替 Help 显示。</summary>
    public string Error
    {
        get => (string)GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }

    public SettingsRow()
    {
        Margin = new Thickness(0, 0, 0, 12);

        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.Margin = new Thickness(0, 0, 16, 0);
        _label.TextWrapping = TextWrapping.Wrap;
        _label.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeBody");
        _label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        _host.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumn) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_label, 0);
        Grid.SetColumn(_host, 1);
        grid.Children.Add(_label);
        grid.Children.Add(_host);

        foreach (var assist in new[] { _help, _error })
        {
            assist.TextWrapping = TextWrapping.Wrap;
            assist.Margin = new Thickness(LabelColumn + 16, 4, 0, 0);
            assist.Visibility = Visibility.Collapsed;
            assist.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
        }
        _help.SetResourceReference(TextBlock.ForegroundProperty, "TextHint");
        _error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");

        Children.Add(grid);
        Children.Add(_help);
        Children.Add(_error);

        _label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Label)) { Source = this });
        _host.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(Content)) { Source = this });
        _help.SetBinding(TextBlock.TextProperty, new Binding(nameof(Help)) { Source = this });
        _error.SetBinding(TextBlock.TextProperty, new Binding(nameof(Error)) { Source = this });
    }

    /// <summary>说明行出现/消失时保持辅助文案缩进与标签列对齐（ContentProperty 无关，纯视觉）。</summary>
    private static void OnHelpChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (SettingsRow)d;
        row._help.Visibility = string.IsNullOrEmpty(row.Help) ? Visibility.Collapsed : Visibility.Visible;
        OnAssistChanged(row, e);
    }

    private static void OnAssistChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 错误优先于说明显示：校验出错时说明让位，改好后自动回来
        var row = (SettingsRow)d;
        bool hasError = !string.IsNullOrEmpty(row.Error);
        row._error.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        row._help.Visibility = !hasError && !string.IsNullOrEmpty(row.Help)
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
