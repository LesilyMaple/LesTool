using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Globalization;
using System.Windows.Data;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.ClipboardHistory;

/// <summary>小节标题行（已固定 / 历史）——列表里的不可选行。</summary>
public sealed record HeaderRow(string Title);

/// <summary>行模板选择：标题行与历史行分模板（保虚拟化，不用分组视图）。</summary>
public sealed class ClipTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? ItemTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is HeaderRow ? HeaderTemplate : ItemTemplate;
}

/// <summary>历史条目 → 预览缩略图（经历史层缓存；虚拟化下只对已呈现行解码）。</summary>
public sealed class PreviewConverter : IValueConverter
{
    private readonly ClipboardHistory _history;
    public PreviewConverter(ClipboardHistory history) => _history = history;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ClipItem item ? _history.LoadPreview(item) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>列表类别（搜索条下 Tab；文本 Tab 不含链接——链接单列一档）。</summary>
public enum ClipTab
{
    All,
    Text,
    Image,
    Files,
    Link,
}

/// <summary>
/// 剪切板历史浮窗：搜索 + 类别 Tab + 虚拟化列表（固定区置顶）+ 键盘导航
/// （↑↓/Enter 粘贴、Ctrl+Enter 纯文本、Ctrl+点击多选合并、Esc 逐级退）。
/// 窗口协议沿音量混音器：预创建 HWND 预热、光标所在显示器顶部居中、失焦/Esc 淡出。
/// 呼出前抓目标窗口句柄（优先命令链带来的快照原前台），回贴靠它把焦点送回去。
/// </summary>
public partial class ClipboardPanelWindow : Window
{
    private const int ActivateGuardMs = 250;
    private const double TopOffsetRatio = 1.0 / 6.0;

    private readonly ClipboardHistory _history;
    private readonly PasteBack _paste;
    private readonly List<object> _rows = new(); // HeaderRow / ClipItem（过滤结果）

    private Point _anchorPx;      // 呼出时光标物理坐标（重摆基准）
    private nint _targetHwnd;     // 呼出前的前台窗口（回贴目标）
    private bool _closing;        // 淡出中：忽略 Deactivated
    private int _shownAt;         // 显示时刻：激活未稳的窗口期忽略失焦
    private int _selected;        // 键盘游标：_rows 里第几个 ClipItem（0 起）
    private ClipTab _tab = ClipTab.All;

    /// <summary>淡出收起完成（插件在此落盘 + 清预览缓存）。</summary>
    public event Action? Hidden;

    public ClipboardPanelWindow(ClipboardHistory history, IPluginSettings settings)
    {
        _history = history;
        _paste = new PasteBack(settings, history);
        Resources["PreviewConverter"] = new PreviewConverter(history); // 先注入再解析 XAML，行模板取用万无一失
        InitializeComponent();
        Closed += (_, _) => { _closing = true; Root.BeginAnimation(UIElement.OpacityProperty, null); };

        DpiChanged += (_, _) =>
        {
            if (IsVisible && !_closing)
                Reposition();
        };
    }

    /// <summary>预创建 HWND 预热（Root 透明不闪内容），触发只做定位 + Show。</summary>
    public void PrepareForFastShow()
    {
        Show();
        Hide();
    }

    /// <summary>命令开关。淡出途中再触发视为「继续收」。target = 命令链带来的原前台句柄。</summary>
    public void Toggle(nint target)
    {
        if (IsVisible && !_closing) HideAnimated();
        else Open(target);
    }

    /// <summary>弹出：目标句柄优先用命令链快照（命令面板路径的失准修正），否则临场取前台。</summary>
    public void Open(nint target)
    {
        _targetHwnd = target != 0 && IsWindow(target) ? target : GetForegroundWindow();
        Log.Debug("剪切板面板回贴目标窗口：{Handle}", _targetHwnd);

        _closing = false;
        _shownAt = Environment.TickCount;
        _anchorPx = GdiCapture.GetCursorPosition();

        foreach (var item in _history.Items) item.Marked = false; // 上次会话的多选标记不跨次
        TabAll.IsChecked = true; // Tab 回到「全部」（未在窗口期触发的 Checked 由下方 RebuildRows 兜底）
        SearchInput.Text = ""; // 空串不触发 TextChanged，提示语手动同步
        SearchHint.Visibility = Visibility.Visible;
        RebuildRows();

        UpdateLayout(); // SizeToContent 高度就绪后再定位
        Reposition();

        Root.Opacity = 0;
        Show();
        Activate();
        SearchInput.Focus();
        SearchInput.CaretIndex = SearchInput.Text.Length;
        Root.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));
        Log.Information("剪切板面板已呼出：{Count} 条", _history.Items.Count);
    }

    /// <summary>淡出后收起（150ms），完成广播 Hidden。</summary>
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

    /// <summary>淡出收起中（Toggle 在此期间把触发视为「继续收」）。</summary>
    public bool IsClosing => _closing;

    /// <summary>按锚点显示器的工作区把窗摆到顶部居中（命令面板/混音器同款：上 1/6 处，向下生长）。</summary>
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
            x = _anchorPx.X - w / 2; // 拿不到工作区（极端情形）：退化为以锚点为心
            y = _anchorPx.Y - h / 2;
        }
        else
        {
            x = work.Left + (work.Width - w) / 2;
            y = work.Top + work.Height * TopOffsetRatio;
        }
        GdiCapture.MoveWindowPhys(hwnd, new Rect(x, y, w, h));
    }

    // ---------- 列表构建与过滤 ----------

    /// <summary>重建可见行：类别 Tab + 搜索词双过滤；固定区（带节标题）→ 历史区（带节标题）；
    /// 回选刚才选中的条目，不在了退回首条。</summary>
    private void RebuildRows()
    {
        var prefer = HistoryList.SelectedItem as ClipItem;

        _rows.Clear();
        var query = SearchInput.Text.Trim();
        bool InTab(ClipItem i) => _tab switch
        {
            ClipTab.Text => i.Kind == ClipKind.Text && !i.IsLink,
            ClipTab.Image => i.Kind == ClipKind.Image,
            ClipTab.Files => i.Kind == ClipKind.Files,
            ClipTab.Link => i.IsLink,
            _ => true,
        };
        bool Matches(ClipItem i) => query.Length == 0
            || (i.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (i.SourceApp?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (i.Files?.Any(f => f.Contains(query, StringComparison.OrdinalIgnoreCase)) ?? false);

        var pinned = _history.Items.Where(i => i.Pinned && InTab(i) && Matches(i)).ToList();
        var normal = _history.Items.Where(i => !i.Pinned && InTab(i) && Matches(i)).ToList();
        if (pinned.Count > 0) _rows.Add(new HeaderRow("已固定"));
        _rows.AddRange(pinned);
        if (normal.Count > 0) _rows.Add(new HeaderRow("历史"));
        _rows.AddRange(normal);

        HistoryList.ItemsSource = null; // 重挂强刷（量在虚拟化之下，无感）
        HistoryList.ItemsSource = _rows;

        var itemRows = ItemRowIndices();
        var row = prefer is not null ? _rows.IndexOf(prefer) : -1;
        if (row < 0) row = itemRows.Count > 0 ? itemRows[0] : -1;

        HistoryList.SelectedIndex = row;
        _selected = row >= 0 ? itemRows.IndexOf(row) : -1;
        if (row >= 0) HistoryList.ScrollIntoView(HistoryList.SelectedItem);

        var hasItems = itemRows.Count > 0;
        EmptyHint.Text = _history.Items.Count == 0
            ? "暂无记录，复制点什么试试"
            : query.Length > 0 ? "无匹配内容" : "该类别暂无内容";
        EmptyHint.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        HistoryList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>_rows 里全部 ClipItem 的行号（跳过节标题）。</summary>
    private List<int> ItemRowIndices()
    {
        var indices = new List<int>();
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i] is ClipItem)
                indices.Add(i);
        return indices;
    }

    // ---------- 交互 ----------

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildRows();
    }

    /// <summary>类别 Tab 切换（Tag 即枚举名）。窗口未显示时只记状态不重建（Open 里统一兜底）。</summary>
    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse(tag, ignoreCase: true, out ClipTab tab))
            _tab = tab;
        if (IsVisible) RebuildRows();
        Log.Debug("类别 Tab 切换：{Tab}", _tab);
    }

    /// <summary>键盘：↑↓ 循环选中（焦点留在搜索框）、Enter 粘贴（有勾选则合并多条）、
    /// Esc 逐级退（先清勾选，再清搜索词，再收面板）。IME 组合期间不抢键。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed) return;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                var marked = MarkedItems();
                if (marked.Count > 0) foreach (var item in marked) item.Marked = false;
                else if (SearchInput.Text.Length > 0) SearchInput.Text = "";
                else HideAnimated();
                break;
            case Key.Down:
            case Key.Up:
            {
                var itemRows = ItemRowIndices();
                if (itemRows.Count == 0) return;
                e.Handled = true;
                var delta = e.Key == Key.Down ? 1 : -1;
                _selected = (_selected + delta + itemRows.Count) % itemRows.Count;
                HistoryList.SelectedIndex = itemRows[_selected];
                HistoryList.ScrollIntoView(HistoryList.SelectedItem);
                break;
            }
            case Key.Enter:
            {
                e.Handled = true;
                var plain = Keyboard.Modifiers.HasFlag(ModifierKeys.Control); // Ctrl+Enter = 丢弃富文本格式
                var toPaste = MarkedItems();
                if (toPaste.Count == 1) ActivateItem(toPaste[0], plain);
                else if (toPaste.Count > 1) MergePaste(toPaste);
                else if (HistoryList.SelectedItem is ClipItem item) ActivateItem(item, plain);
                break;
            }
        }
    }

    private List<ClipItem> MarkedItems() => _history.Items.Where(i => i.Marked).ToList();

    private void OnRowActivate(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ClipItem item }) return;
        // Ctrl+点击 = 多选合并的勾选（再点取消），不触发粘贴；普通点击 = 直接粘贴
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            item.Marked = !item.Marked;
            Log.Debug("多选勾选{State}：{Kind}", item.Marked ? "添加" : "取消", item.Kind);
            return;
        }
        ActivateItem(item);
    }

    /// <summary>行内 T 按钮：富文本条目按纯文本粘贴（等效 Ctrl+Enter）。</summary>
    private void OnPastePlain(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ClipItem item }) return;
        e.Handled = true;
        ActivateItem(item, plain: true);
    }

    /// <summary>合并粘贴：多条文本按行拼接（文件合并清单），面板收起后直接写剪贴板并回贴，不产生新条目。</summary>
    private void MergePaste(List<ClipItem> marked)
    {
        Log.Information("合并粘贴：{Count} 条", marked.Count);
        HideAnimated();
        _paste.ExecuteMerged(marked, _targetHwnd);
    }

    private void OnPinToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ClipItem item }) return;
        e.Handled = true;
        _history.TogglePin(item);
        RebuildRows();
    }

    private void OnDeleteRow(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ClipItem item }) return;
        e.Handled = true;
        _history.Delete(item);
        RebuildRows();
    }

    /// <summary>粘贴条目：先收面板让位焦点，再回写剪贴板 + 回贴，最后提到本组顶部。
    /// plain = 丢弃富文本格式只贴纯文本。</summary>
    private void ActivateItem(ClipItem item, bool plain = false)
    {
        Log.Debug("粘贴历史条目：{Kind}（来源 {App}，纯文本 {Plain}）", item.Kind, item.SourceApp ?? "未知", plain);
        HideAnimated();
        if (_paste.Execute(item, _targetHwnd, plain))
            _history.TouchOnPaste(item);
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        if (_closing) return;
        if (Environment.TickCount - _shownAt < ActivateGuardMs) return;
        HideAnimated();
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);
}
