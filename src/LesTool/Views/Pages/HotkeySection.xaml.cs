using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LesTool.Platform;
using LesTool.Services;

namespace LesTool.Views.Pages;

/// <summary>
/// 标准热键区：每个可绑热键的命令一行（命令名 + 键位；键位点击进入捕获，低级钩子
/// 拦得到已被占用的组合），Backspace 清除、Esc 取消，提交即保存并全量重注册热键；
/// 一命令一键位。捕获/冲突检测/注册注销复用全局链路（HotkeyOps），插件作者零成本。
/// 2026-10-31 二改：插件页的热键配置迁入页头小部件（PluginHotkeyButton），本控件
/// 仍服务命令面板键位页与页头小部件的下拉内部（compact 模式：藏标题、零外边距）。
/// </summary>
public partial class HotkeySection : UserControl
{
    private readonly string _prefix;
    private readonly List<CommandRow> _rows = new();

    /// <summary>任一命令键位变化后触发（页头小部件订阅以刷新汇总文案）。</summary>
    public event Action? Changed;

    public HotkeySection(string idPrefix) : this(idPrefix, showHeader: true)
    {
    }

    /// <param name="showHeader">false = 紧凑模式（嵌在弹层里）：藏"快捷键"标题、根边距归零，保留操作说明。</param>
    public HotkeySection(string idPrefix, bool showHeader)
    {
        _prefix = idPrefix;
        InitializeComponent();
        if (!showHeader)
        {
            SectionTitle.Visibility = Visibility.Collapsed;
            RootPanel.Margin = new Thickness(0);
        }
        foreach (var command in ActionRegistry.ForScenario(CommandScenarios.Hotkey)
                     .Where(c => c.Id.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new CommandRow(this, command, compact: !showHeader);
            _rows.Add(row);
            RowsPanel.Children.Add(row);
        }
        RefreshStatuses();
    }

    /// <summary>当前键位（多绑定遗留取第一条；空 = 未绑定）。</summary>
    private string KeysOf(string commandId) => HotkeyOps.KeysOf(commandId);

    /// <summary>提交某命令的键位（"" = 解绑）。行与外部共用 HotkeyOps 一条链路。</summary>
    private void Commit(string commandId, string keys)
    {
        HotkeyOps.Commit(commandId, keys);
        RefreshStatuses();
        Changed?.Invoke();
    }

    private void RefreshStatuses()
    {
        foreach (var row in _rows)
        {
            var keys = KeysOf(row.Command.Id);
            row.SyncVisuals();
            if (keys == "")
            {
                row.ApplyStatus(null);
                continue;
            }
            // 外部占用以 HotkeyService 真实注册结果为准
            row.ApplyStatus(HotkeyOps.SystemConflict(row.Command.Id)
                ? "已被其他程序占用（或为系统保留组合），此快捷键未生效"
                : null);
        }
    }

    /// <summary>单命令一行：命令名 + 键位控件；捕获态内联在键位里，冲突提示在行下。
    /// compact（嵌弹层）：键位按钮 120 宽、行距 8；整页形态键位 200、行距 12。
    /// 字号/颜色一律资源键（代码构建 UI 门禁扫不到，四档刻度靠自觉）。</summary>
    private sealed class CommandRow : UserControl
    {
        public LesCommand Command { get; }
        private readonly HotkeySection _owner;
        private readonly Button _keyButton;
        private readonly TextBlock _status = new()
        {
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        private HotkeyCapture? _capture;

        public CommandRow(HotkeySection owner, LesCommand command, bool compact)
        {
            _owner = owner;
            Command = command;
            _keyButton = new Button
            {
                MinWidth = compact ? 120 : 200,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            _status.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeCaption");
            _status.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");

            _keyButton.Click += (_, _) => EnterCapture();
            _keyButton.LostFocus += (_, _) => ExitCapture(); // 点去别处 = 放弃
            Unloaded += (_, _) => ExitCapture(); // 任何卸载路径必须退钩子

            // 行型：动作名左、键位靠右（2026-11-01 用户确认保持此形态）
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = command.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeBody");
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            Grid.SetColumn(label, 0);
            Grid.SetColumn(_keyButton, 1);
            grid.Children.Add(label);
            grid.Children.Add(_keyButton);

            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, compact ? 8 : 12) };
            panel.Children.Add(grid);
            panel.Children.Add(_status);
            Content = panel;
        }

        public void SyncVisuals()
        {
            if (_capture is not null) return;
            _keyButton.Content = HotkeyOps.FormatKeys(_owner.KeysOf(Command.Id));
        }

        /// <summary>行下提示；null 清除。</summary>
        public void ApplyStatus(string? message)
        {
            _status.Text = message ?? "";
            _status.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------- 捕获（HotkeyCapture：低级钩子优先，WPF 兜底） ----------

        private void EnterCapture()
        {
            if (_capture is not null) return;
            ApplyStatus(null);
            _keyButton.Content = "按下组合键…  Esc 取消";
            _capture = new HotkeyCapture(this);
            _capture.KeyIntercepted += HandleCaptureKey;
            _capture.ModifiersChanged += modifiers =>
            {
                // 实时预览：按住什么修饰键就显示什么，松开消回
                if (_capture is null) return;
                _keyButton.Content = modifiers == ModifierKeys.None
                    ? "按下组合键…  Esc 取消"
                    : HotkeyOps.PreviewText(modifiers);
            };
            _capture.Start();
            _keyButton.Focus();
        }

        private void ExitCapture()
        {
            _capture?.Dispose();
            _capture = null;
            SyncVisuals();
        }

        private void HandleCaptureKey(Key key, ModifierKeys modifiers)
        {
            if (_capture is null) return;
            if (key is Key.DeadCharProcessed or Key.None) return;

            if (key == Key.Escape)
            {
                ExitCapture();
                return;
            }

            if (key == Key.Back)
            {
                ExitCapture();
                if (_owner.KeysOf(Command.Id) != "")
                    _owner.Commit(Command.Id, ""); // Backspace = 清除绑定
                return;
            }

            if (HotkeyOps.IsModifierKey(key)) return;

            string? reject = HotkeyOps.Validate(Command.Id, key, modifiers, out var keys);
            if (reject is not null)
            {
                ApplyStatus(reject);
                return;
            }

            ExitCapture();
            _owner.Commit(Command.Id, keys);
        }
    }
}
