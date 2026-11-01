using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.SmartSearch;

/// <summary>
/// 智能搜索浮窗：输入条与候选列表共用一窗的两模式窗口（2026-10-29 设计拍板）。
/// 输入模式（触发时无选中文字）：输入后回车识别；前缀+空格直用引擎（如 gh）；
/// 结尾空格列出全部引擎挑选。列表模式：多候选（或选中文字直接命中多条）时 ↑↓/Enter 选。
/// 文字识别与候选构建全部在 RecognizerPipeline 侧完成，窗口只负责展示与键盘导航。
/// 窗口协议沿剪切板面板：预创建 HWND 预热、光标附近弹出（下方放不下翻上方）、
/// 失焦/Esc 淡出（150ms）、可见中再触发视为收起。
/// </summary>
public partial class SmartSearchWindow : Window
{
    private const int ActivateGuardMs = 250;
    private const double AnchorOffsetDip = 16;

    private readonly RecognizerPipeline _pipeline;
    private readonly ILesHost _host;
    private readonly List<SmartCandidate> _candidates = new();

    private Point _anchorPx;   // 触发时光标物理坐标（弹窗与重摆基准）
    private bool _closing;     // 淡出中：忽略 Deactivated
    private int _shownAt;      // 显示时刻：激活未稳的窗口期忽略失焦
    private int _selected;     // 键盘游标（_candidates 索引，0 起）

    public SmartSearchWindow(RecognizerPipeline pipeline, ILesHost host)
    {
        _pipeline = pipeline;
        _host = host;
        InitializeComponent();
        Closed += (_, _) => { _closing = true; Root.BeginAnimation(UIElement.OpacityProperty, null); };

        DpiChanged += (_, _) =>
        {
            if (IsVisible && !_closing)
                PlaceAtAnchor();
        };
    }

    /// <summary>预创建 HWND 预热（Root 透明不闪内容），触发只做定位 + Show。</summary>
    public void PrepareForFastShow()
    {
        Show();
        Hide();
    }

    /// <summary>命令入口：可见即收（toggle 语义），否则按有无选中文字进输入/列表模式。</summary>
    public void Run(string? selectedText, Point cursorPx)
    {
        if (IsVisible && !_closing)
        {
            HideAnimated();
            return;
        }
        _anchorPx = cursorPx;
        var word = selectedText?.Trim();
        if (string.IsNullOrEmpty(word))
            ShowInput();
        else
            Route(word);
    }

    // ---------- 模式切换 ----------

    /// <summary>输入模式：光标处弹输入条等打字。</summary>
    private void ShowInput()
    {
        _candidates.Clear();
        InputPanel.Visibility = Visibility.Visible;
        ListPanel.Visibility = Visibility.Collapsed;
        FooterHint.Text = "Enter 识别执行 · 前缀+空格 指定引擎（如 gh）· 结尾空格 列出引擎 · Esc 关闭";
        QueryInput.Text = ""; // 空串不触发 TextChanged，提示语手动同步
        InputHint.Visibility = Visibility.Visible;
        ShowWindowAndPlace();
        QueryInput.Focus();
        QueryInput.CaretIndex = QueryInput.Text.Length;
        Log.Information("智能搜索输入条已呼出");
    }

    /// <summary>识别路由：引擎前缀直用（不出候选）；唯一候选直办；多个进列表模式。
    /// 命中详情与兜底日志由管线侧记录（日志约定：状态变更 INFO）。</summary>
    private void Route(string word)
    {
        if (_pipeline.TryPrefixRoute(word) is { } direct)
        {
            InvokeCandidate(direct);
            return;
        }

        var candidates = _pipeline.Recognize(word);
        if (candidates.Count == 1)
        {
            InvokeCandidate(candidates[0]);
            return;
        }
        ShowList(candidates);
    }

    /// <summary>列表模式：展示候选清单。</summary>
    private void ShowList(IReadOnlyList<SmartCandidate> candidates)
    {
        _candidates.Clear();
        _candidates.AddRange(candidates);
        InputPanel.Visibility = Visibility.Collapsed;
        ListPanel.Visibility = Visibility.Visible;
        FooterHint.Text = "↑↓ 选择 · Enter 执行 · Esc 关闭";

        CandidateList.ItemsSource = null; // 重挂强刷（候选量小，无感）
        CandidateList.ItemsSource = _candidates;
        _selected = 0;
        if (_candidates.Count > 0)
        {
            CandidateList.SelectedIndex = 0;
            CandidateList.ScrollIntoView(CandidateList.SelectedItem);
        }

        var wasVisible = IsVisible;
        ShowWindowAndPlace();
        if (wasVisible) Focus(); // 输入模式转列表：输入条折叠后焦点收回窗口，键盘导航才能续上
        Log.Information("智能搜索候选窗：{Count} 条", _candidates.Count);
    }

    /// <summary>显示 + 定位 + 淡入（Root 常态 Opacity=0 防闪现）。</summary>
    private void ShowWindowAndPlace()
    {
        _closing = false;
        _shownAt = Environment.TickCount;

        UpdateLayout(); // SizeToContent 高度就绪后再定位
        PlaceAtAnchor();

        Root.Opacity = 0;
        Show();
        Activate();
        Root.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));
    }

    /// <summary>淡出后收起（150ms）。public：插件 Shutdown 收摊用。</summary>
    public void HideAnimated()
    {
        if (!IsVisible) return;
        _closing = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) =>
        {
            Hide();
            _closing = false;
        };
        Root.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>把窗摆到光标右下方（16 DIP 偏移），下方放不下翻上方，再夹回工作区。
    /// 物理坐标直摆（窗口铁律一：不走 DIP Left/Top）。</summary>
    private void PlaceAtAnchor()
    {
        if (!IsLoaded) return;

        nint hwnd = new WindowInteropHelper(this).Handle;
        double scale = GdiCapture.GetMonitorScale(_anchorPx);
        Rect work = GdiCapture.GetMonitorWorkArea(_anchorPx);

        UpdateLayout();
        double w = ActualWidth * scale;
        double h = ActualHeight * scale;
        double x = _anchorPx.X + AnchorOffsetDip * scale;
        double y = _anchorPx.Y + AnchorOffsetDip * scale;

        if (!work.IsEmpty)
        {
            if (y + h > work.Bottom) y = _anchorPx.Y - h - AnchorOffsetDip * scale;
            x = Math.Max(work.Left, Math.Min(x, work.Right - w));
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - h));
        }
        GdiCapture.MoveWindowPhys(hwnd, new Rect(x, y, w, h));
    }

    // ---------- 交互 ----------

    private void OnQueryTextChanged(object sender, TextChangedEventArgs e)
    {
        InputHint.Visibility = QueryInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>键盘：输入模式 Enter 路由（结尾空格列引擎）、列表模式 ↑↓/Enter；
    /// Esc 两种模式都收；IME 组合期间不抢键。</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed) return;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                HideAnimated();
                break;

            case Key.Enter when InputPanel.Visibility == Visibility.Visible:
                e.Handled = true;
                OnEnterFromInput();
                break;

            case Key.Enter:
                e.Handled = true;
                if (CandidateList.SelectedItem is SmartCandidate candidate)
                    InvokeCandidate(candidate);
                break;

            case Key.Down:
            case Key.Up when ListPanel.Visibility == Visibility.Visible:
            {
                if (_candidates.Count == 0) return;
                e.Handled = true;
                var delta = e.Key == Key.Down ? 1 : -1;
                _selected = (_selected + delta + _candidates.Count) % _candidates.Count;
                CandidateList.SelectedIndex = _selected;
                CandidateList.ScrollIntoView(CandidateList.SelectedItem);
                break;
            }
        }
    }

    /// <summary>输入模式回车：空串收窗；结尾空格列引擎；其余走识别路由。</summary>
    private void OnEnterFromInput()
    {
        var raw = QueryInput.Text;
        var word = raw.Trim();
        if (word.Length == 0)
        {
            HideAnimated();
            return;
        }
        if (raw.EndsWith(' ')) ShowList(_pipeline.BuildEngineChooser(word));
        else Route(word);
    }

    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SmartCandidate candidate })
            InvokeCandidate(candidate);
    }

    /// <summary>执行候选动作：先收窗（动作常开外部程序抢焦点），异常兜底 Toast。</summary>
    private void InvokeCandidate(SmartCandidate candidate)
    {
        Log.Information("智能搜索执行：{Title}", candidate.Title);
        HideAnimated();
        try
        {
            candidate.Execute();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "候选动作失败：{Title}", candidate.Title);
            _host.Toast("执行失败，详见日志", 3000);
        }
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        if (_closing) return;
        if (Environment.TickCount - _shownAt < ActivateGuardMs) return;
        HideAnimated();
    }
}
