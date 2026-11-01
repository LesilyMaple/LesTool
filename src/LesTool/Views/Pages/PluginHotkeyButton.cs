using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LesTool.Platform;
using LesTool.Services;

namespace LesTool.Views.Pages;

/// <summary>
/// 插件页头键位小部件（PLUGIN_UI_SPEC A，2026-10-31 用户定案：热键配置上页头）：
/// - 单动作：按钮直接显示键位组合（未绑定），点击进入捕获，一步改；
/// - 多动作：按钮显示汇总（未绑定 / 已绑 N/M），点击弹下拉逐条配（下拉内嵌
///   紧凑版 HotkeySection，行内冲突红字不缩水）；
/// - 无可绑命令（如空格预览走焦点触发）：整件不显示。
/// 显示语义拍板过"汇总式"：不显示"第一个动作的键"——第一个是注册顺序，用户
/// 不可见，显示具体键会张冠李戴。捕获/校验/提交与热键区共用 HotkeyOps 一条链路。
/// </summary>
internal sealed class PluginHotkeyButton : Button
{
    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");

    private readonly string _pluginId;
    private readonly List<LesCommand> _commands;
    private LesCommand? _single;             // 单动作直改模式（null = 多动作下拉模式）
    private HotkeyCapture? _capture;
    private Popup? _popup;
    private HotkeySection? _popupSection;

    public PluginHotkeyButton(string pluginId)
    {
        _pluginId = pluginId;
        _commands = ActionRegistry.ForScenario(CommandScenarios.Hotkey)
            .Where(c => c.Id.StartsWith(pluginId + ".", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (_commands.Count == 0)
        {
            Visibility = Visibility.Collapsed; // 无可绑命令：不显示空壳
            return;
        }
        _single = _commands.Count == 1 ? _commands[0] : null;

        MinWidth = 96;
        Cursor = Cursors.Hand;
        // WPF 铁律（2026-11-01 页头键位钮实锤）：隐式样式只认精确类型、不继承子类——
        // 本类继承 Button 拿不到 TargetType=Button 的隐式样式，会以系统原生灰按钮出街
        // （下拉里的键位钮是纯 Button 所以正常）。显式挂回 Button 隐式样式即恢复制服。
        SetResourceReference(StyleProperty, typeof(Button));
        ToolTip = _single is null
            ? $"共 {_commands.Count} 个动作可配快捷键，点击展开逐条设置"
            : "点击修改快捷键：按下组合键即绑定；Backspace 清除、Esc 取消";
        Click += OnClick;
        Unloaded += (_, _) => ExitCapture();
        Refresh();
    }

    private void Refresh()
    {
        if (_capture is not null) return; // 捕获态的实时预览优先
        if (_single is not null)
            SetContent(HotkeyOps.FormatKeys(HotkeyOps.KeysOf(_single.Id)), mono: true);
        else
        {
            int bound = _commands.Count(c => HotkeyOps.KeysOf(c.Id) != "");
            SetContent(bound == 0 ? "未绑定" : $"已绑 {bound}/{_commands.Count}", mono: false);
        }
    }

    /// <summary>按钮文案 = 键盘字形 + 正文/等宽文字（键位组合走 Consolas，DESIGN 数字键位规范）。</summary>
    private void SetContent(string text, bool mono)
    {
        var glyph = new TextBlock { Text = "\uE92E", FontFamily = IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        label.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeBody");
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        if (mono) label.FontFamily = new FontFamily("Consolas");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(glyph);
        panel.Children.Add(label);
        Content = panel;
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (_single is not null) EnterCapture();
        else OpenPopup();
    }

    // ---------- 多动作：下拉逐条配 ----------

    private void OpenPopup()
    {
        if (_popup is not null)
        {
            _popup.IsOpen = true;
            return;
        }

        _popupSection = new HotkeySection(_pluginId + ".", showHeader: false) { MinWidth = 320 };
        _popupSection.Changed += Refresh;

        // 弹层家法（DESIGN.md / RadialGesturePage 同款）：内边距 8、无阴影、1px 描边、
        // 圆角 overlay 档；右下留白防贴屏边裁切
        var border = new Border
        {
            Child = new ScrollViewer
            {
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _popupSection,
            },
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 12, 16),
            BorderThickness = new Thickness(1),
        };
        border.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "CardBorderColor");
        border.SetResourceReference(Border.CornerRadiusProperty, "CornerRadiusOverlay");

        // 弹层未捕获键时 Esc 关闭（行捕获中 Esc 被行吃掉 = 只取消捕获，弹层保留）
        border.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) _popup!.IsOpen = false;
        };

        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = border,
        };
        _popup.Closed += (_, _) => Refresh();
        _popup.Opened += (_, _) =>
        {
            // 右对齐弹层到按钮右缘（Bottom 默认左对齐，按钮在页头右侧会伸太远）；
            // 首帧布局未完成时 ActualWidth 为 0，跳过（下帧再开的场景极少）
            if (border.ActualWidth > 0)
                _popup.HorizontalOffset = ActualWidth - border.ActualWidth;
        };
        _popup.IsOpen = true;
    }

    // ---------- 单动作：直接捕获 ----------

    private void EnterCapture()
    {
        if (_capture is not null || _single is null) return;
        SetContent("按下组合键…", mono: false);
        ToolTip = null; // 捕获态提示走按钮文案，避免旧 tooltip 悬浮干扰
        _capture = new HotkeyCapture(this);
        _capture.KeyIntercepted += HandleCaptureKey;
        _capture.ModifiersChanged += modifiers =>
        {
            if (_capture is null) return;
            SetContent(modifiers == ModifierKeys.None ? "按下组合键… Esc 取消" : HotkeyOps.PreviewText(modifiers), mono: false);
        };
        _capture.Start();
        Focus();
    }

    private void ExitCapture()
    {
        _capture?.Dispose();
        _capture = null;
        ToolTip = "点击修改快捷键：按下组合键即绑定；Backspace 清除、Esc 取消";
        Refresh();
    }

    private void HandleCaptureKey(Key key, ModifierKeys modifiers)
    {
        if (_capture is null || _single is null) return;
        if (key is Key.DeadCharProcessed or Key.None) return;

        if (key == Key.Escape)
        {
            ExitCapture();
            return;
        }
        if (key == Key.Back)
        {
            ExitCapture();
            if (HotkeyOps.KeysOf(_single.Id) != "")
                CommitSingle("");
            return;
        }
        if (HotkeyOps.IsModifierKey(key)) return;

        string? reject = HotkeyOps.Validate(_single.Id, key, modifiers, out var keys);
        if (reject is not null)
        {
            SetContent(reject, mono: false);
            return;
        }
        ExitCapture();
        CommitSingle(keys);
    }

    private void CommitSingle(string keys)
    {
        HotkeyOps.Commit(_single!.Id, keys);
        Refresh();
        if (keys != "" && HotkeyOps.SystemConflict(_single.Id))
            ToastService.Show("此快捷键已被其他程序占用（或为系统保留组合），未生效", 3500);
    }
}
