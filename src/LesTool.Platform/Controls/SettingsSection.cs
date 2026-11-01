using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;

namespace LesTool.Platform.Controls;

/// <summary>
/// 设置区块（PLUGIN_UI_SPEC）：标题（Body SemiBold，与热键区同款）+ 可选一句说明
/// （caption 弱色，换行）+ 内容。插件设置页的配置区只能由本控件堆叠而成，
/// 区块间距由默认上边距 32 提供（Law 5：组外距 > 组内距）。主题取值全部
/// SetResourceReference（等价 DynamicResource），皮肤热重载安全。
/// </summary>
[ContentProperty(nameof(Content))]
public class SettingsSection : StackPanel
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingsSection), new PropertyMetadata(""));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(SettingsSection),
        new PropertyMetadata("", OnCaptionChanged));

    public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(
        nameof(Content), typeof(object), typeof(SettingsSection), new PropertyMetadata(null));

    private readonly TextBlock _title = new();
    private readonly TextBlock _caption = new();
    private readonly ContentPresenter _host = new();

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>区块内容（通常是一列 SettingsRow / CheckRow，或列表块容器）。</summary>
    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    public SettingsSection()
    {
        Margin = new Thickness(0, 32, 0, 0);

        _title.FontWeight = FontWeights.SemiBold;
        _title.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeBody");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

        _caption.TextWrapping = TextWrapping.Wrap;
        _caption.Margin = new Thickness(0, 4, 0, 0);
        _caption.Visibility = Visibility.Collapsed;
        _caption.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "TextHint");

        _host.Margin = new Thickness(0, 8, 0, 0);

        Children.Add(_title);
        Children.Add(_caption);
        Children.Add(_host);

        _title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this });
        _caption.SetBinding(TextBlock.TextProperty, new Binding(nameof(Caption)) { Source = this });
        _host.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(Content)) { Source = this });
    }

    /// <summary>说明行出现/消失时同步内容区的呼吸空间：有说明 12、无说明 8。</summary>
    private static void OnCaptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var section = (SettingsSection)d;
        bool hasCaption = !string.IsNullOrEmpty(section.Caption);
        section._caption.Visibility = hasCaption ? Visibility.Visible : Visibility.Collapsed;
        section._host.Margin = new Thickness(0, hasCaption ? 12 : 8, 0, 0);
    }
}
