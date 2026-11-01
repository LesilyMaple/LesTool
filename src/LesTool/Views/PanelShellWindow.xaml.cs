using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LesTool.Services;
using LesTool.Platform;
using Serilog;

namespace LesTool.Views;

/// <summary>
/// 面板壳窗口（uTools 式）：上命令面板（搜索输入条）+ 中搜索清单（全局命令注册表）
/// + 下页脚提示。无边框透明窗、抢焦点、光标所在显示器顶部居中。
/// 键盘：↑↓ 循环选中（焦点保持在输入框，选中态只刷描边）、Enter 执行选中并收起、
/// Esc 收起（操作面板接入后升级为逐级退）。关闭三径：Esc / 失焦 / 热键 Toggle。
/// 定位沿轮盘路线：物理像素直摆 + DPI 变化重摆；高度 SizeToContent、清单超 288 DIP 滚动。
/// </summary>
public partial class PanelShellWindow : Window
{
    private const int ActivateGuardMs = 250;
    private const double TopOffsetRatio = 1.0 / 6.0;

    private Point _anchorPx;     // 最近一次呼出时的光标物理坐标（重摆基准）
    private bool _closing;       // 淡出收起中：忽略 Deactivated
    private int _shownAt;        // 显示时刻：Show 后激活未稳的窗口期忽略失焦
    private List<LesCommand> _filtered = new(); // 当前清单（过滤结果，按注册序）
    private int _selectedIndex;  // 键盘选中行（-1 = 无）

    /// <summary>淡出收起完成。</summary>
    public event Action? Hidden;

    public PanelShellWindow()
    {
        InitializeComponent();
        DpiChanged += (_, _) =>
        {
            if (IsVisible && !_closing)
                Reposition();
        };
    }

    /// <summary>预创建 HWND 并完成一次布局渲染；Root 全透明，预热不闪面板。</summary>
    public void PrepareForFastShow()
    {
        Show();
        Hide();
    }

    /// <summary>在光标所在显示器顶部居中呼出。参数为光标物理像素坐标。</summary>
    public void ShowAt(Point cursorPx)
    {
        _anchorPx = cursorPx;
        _closing = false;
        _shownAt = Environment.TickCount;

        CommandInput.Text = ""; // 每次呼出重置搜索（清单在过滤里全量重建）
        InputHint.Visibility = Visibility.Visible; // Text 已是空串时不会触发 TextChanged，手动同步
        ApplyFilter();

        UpdateLayout(); // SizeToContent 高度就绪后再定位
        Reposition();

        Root.Opacity = 0;
        Show();
        Activate();
        CommandInput.Focus();
        CommandInput.CaretIndex = CommandInput.Text.Length;
        Root.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));
        Log.Debug("面板已定位呼出：锚点 {Anchor}，命令 {Count} 条", cursorPx, _filtered.Count);
    }

    /// <summary>淡出后收起（150ms），完成时广播 Hidden。</summary>
    public void HideAnimated()
    {
        if (!IsVisible) return;
        _closing = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) =>
        {
            Hide();
            _closing = false;
            Hidden?.Invoke();
        };
        Root.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>淡出收起中（Toggle 在此期间把热键视为「继续收」）。</summary>
    public bool IsClosing => _closing;

    /// <summary>按锚点显示器的工作区把壳摆到顶部居中。</summary>
    private void Reposition()
    {
        if (!IsLoaded) return;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        double scale = GdiCapture.GetMonitorScale(_anchorPx);
        Rect work = GdiCapture.GetMonitorWorkArea(_anchorPx);

        UpdateLayout();

        double w = ActualWidth * scale;
        double h = ActualHeight * scale;
        double x, y;
        if (work.IsEmpty)
        {
            // 拿不到工作区（极端情形）：退化为以锚点为心
            x = _anchorPx.X - w / 2;
            y = _anchorPx.Y - h / 2;
        }
        else
        {
            x = work.Left + (work.Width - w) / 2;
            y = work.Top + work.Height * TopOffsetRatio;
        }
        GdiCapture.MoveWindowPhys(hwnd, new Rect(x, y, w, h));
    }

    // ---------- 搜索与清单 ----------

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        InputHint.Visibility = CommandInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    /// <summary>按 名称/别名/Id 过滤（不区分大小写）；空输入 = 全量。选中回到第 0 行。</summary>
    private void ApplyFilter()
    {
        var query = CommandInput.Text.Trim();
        _filtered = string.IsNullOrEmpty(query)
            ? ActionRegistry.All.ToList()
            : ActionRegistry.All.Where(c =>
                c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        _selectedIndex = _filtered.Count > 0 ? 0 : -1;
        RenderList();
    }

    private void RenderList()
    {
        CommandList.Children.Clear();
        EmptyHint.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        for (var i = 0; i < _filtered.Count; i++)
        {
            var command = _filtered[i];
            var row = new Button
            {
                Style = (Style)FindResource("CommandItemStyle"),
                Tag = command,
                Content = BuildRowContent(command),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            if (i == _selectedIndex)
                row.BorderBrush = (Brush)FindResource("NavAccent");
            row.Click += (_, _) => ExecuteRow(row, command);
            CommandList.Children.Add(row);
        }
    }

    /// <summary>行内容：字形 + 名称 + 右侧来源徽标（内置/脚本/面板）。</summary>
    private static FrameworkElement BuildRowContent(LesCommand command)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new TextBlock
        {
            Text = command.Glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = (double)App.Current.FindResource("FontSizeBody"),
            Foreground = (Brush)App.Current.FindResource("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(glyph, 0);
        grid.Children.Add(glyph);

        var name = new TextBlock
        {
            Text = command.Name,
            FontSize = (double)App.Current.FindResource("FontSizeBody"),
            Foreground = (Brush)App.Current.FindResource("TextPrimary"),
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var badge = new TextBlock
        {
            Text = command.Kind switch
            {
                CommandKind.Script => "脚本",
                CommandKind.Plugin => "插件",
                CommandKind.Panel => "面板",
                _ => "内置",
            },
            FontSize = (double)App.Current.FindResource("FontSizeCaption"),
            Foreground = (Brush)App.Current.FindResource("TextHint"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);

        return grid;
    }

    // ---------- 键盘导航 ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // IME 组合输入期间不抢键（否则打字过程会被导航/执行打断）
        if (e.Key == Key.ImeProcessed) return;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                HideAnimated(); // 操作面板接入后改为逐级退
                break;
            case Key.Down:
            case Key.Up:
                if (_filtered.Count > 0)
                {
                    e.Handled = true;
                    var delta = e.Key == Key.Down ? 1 : -1;
                    _selectedIndex = (_selectedIndex + delta + _filtered.Count) % _filtered.Count;
                    UpdateSelectionVisuals();
                }
                break;
            case Key.Enter:
                if (_selectedIndex >= 0 && _selectedIndex < _filtered.Count)
                {
                    e.Handled = true;
                    ExecuteRow(null, _filtered[_selectedIndex]);
                }
                break;
        }
    }

    /// <summary>选中态只刷描边（焦点保持在输入框）；同步滚动到可见。</summary>
    private void UpdateSelectionVisuals()
    {
        for (var i = 0; i < CommandList.Children.Count; i++)
        {
            if (CommandList.Children[i] is not Button row) continue;
            // 置 null = 回落到样式默认的透明描边，避免本地值压住样式
            row.BorderBrush = i == _selectedIndex ? (Brush)FindResource("NavAccent") : null;
            if (i == _selectedIndex) row.BringIntoView();
        }
    }

    /// <summary>执行命令并收起（键盘 Enter / 鼠标点击两路）。</summary>
    private void ExecuteRow(Button? row, LesCommand command)
    {
        // 鼠标点击时以点击行为准；键盘路径 row 为 null，索引即选中行
        if (row is not null)
        {
            var index = CommandList.Children.IndexOf(row);
            if (index >= 0) _selectedIndex = index;
        }

        Log.Debug("面板执行命令：{Id}（第 {Index} 行）", command.Id, _selectedIndex);
        HideAnimated();
        ActionRegistry.Execute(command.Id); // 先收起再执行：命令开新窗口抢焦点时面板已让位
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        if (_closing) return;
        if (Environment.TickCount - _shownAt < ActivateGuardMs) return;
        HideAnimated();
    }
}
