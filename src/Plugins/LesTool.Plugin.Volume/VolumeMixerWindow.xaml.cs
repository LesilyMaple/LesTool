using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.Volume;

/// <summary>
/// 托盘音量混音器浮窗（EarTrumpet 式）：设备主音量 + 按应用分组的会话音量。
/// 主屏右下角浮出、失焦/Esc 淡出；可见期间 250ms 轮询刷新（外部改音量同步回来，
/// 鼠标拖动中的滑块跳过回写防跳变）。会话列表无系统通知，sndvol 同样靠轮询。
/// </summary>
public partial class VolumeMixerWindow : Window
{
    private const int ActivateGuardMs = 250;
    private const int PollIntervalMs = 250;
    private const double MarginFromEdge = 12;

    private readonly AudioMixerService _audio;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, AppRow> _rows = new();

    private bool _suppress;    // 程序回写 Slider.Value 时压掉 ValueChanged，防回环
    private bool _closing;     // 淡出中：忽略 Deactivated
    private int _shownAt;      // 显示时刻：激活未稳的窗口期忽略失焦
    private bool _warnedBrokenVolume; // 音量写入失败只 Toast 一次

    /// <summary>淡出收起完成（控制器记日志用）。</summary>
    public event Action? Hidden;

    public VolumeMixerWindow(AudioMixerService audio)
    {
        _audio = audio;
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
        _timer.Tick += (_, _) =>
        {
            try
            {
                _audio.Refresh();
                SyncAll();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "混音器轮询刷新失败");
            }
        };

        // 主滑块与应用滑块一致：滚轮步进 ±2（ValueChanged 走同一写入路径）
        MasterSlider.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            MasterSlider.Value = Math.Clamp(MasterSlider.Value + (e.Delta > 0 ? 2 : -2), 0, 100);
        };
    }

    /// <summary>预创建 HWND 预热（Root 透明不闪内容），触发只做定位 + Show。</summary>
    public void PrepareForFastShow()
    {
        Show();
        Hide();
    }

    /// <summary>弹出：立即刷新一次数据再显示，避免旧值闪现。</summary>
    public void Open()
    {
        _audio.Refresh();
        SyncAll();

        _closing = false;
        _shownAt = Environment.TickCount;
        UpdateLayout();
        PositionBottomRight();

        Root.Opacity = 0;
        Show();
        Activate();
        Root.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));
        _timer.Start();
        Log.Information("音量混音器弹出：{Device}，应用 {Count} 个", _audio.DeviceName, _rows.Count);
    }

    /// <summary>淡出收起（150ms），完成广播 Hidden。</summary>
    public void HideAnimated()
    {
        if (!IsVisible || _closing) return;
        _closing = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) =>
        {
            _timer.Stop();
            Hide();
            _closing = false;
            Hidden?.Invoke();
        };
        Root.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>托盘左键开关。淡出途中再点视为“继续收”（托盘点击常与失足收起竞争）。</summary>
    public void ToggleFromTray()
    {
        if (IsVisible) HideAnimated();
        else Open();
    }

    /// <summary>主屏工作区右下角（托盘旁），底边钉住：列表高度变化时只向上长。</summary>
    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        UpdateLayout();
        Left = area.Right - ActualWidth - MarginFromEdge;
        Top = area.Bottom - ActualHeight - MarginFromEdge;
    }

    // ---------- 数据同步 ----------

    private void SyncAll()
    {
        SyncMaster();

        var alive = new HashSet<string>();
        var index = 0;
        foreach (var app in _audio.Apps)
        {
            alive.Add(app.Key);
            if (!_rows.TryGetValue(app.Key, out var row))
            {
                row = BuildRow(app);
                _rows[app.Key] = row;
                AppList.Children.Add(row.Root);
                Log.Debug("混音器新增应用行：{App}", app.DisplayName);
            }
            // 顺序跟随模型（系统声音置顶 + 名称序），不一致就重排
            if (AppList.Children.IndexOf(row.Root) != index)
            {
                AppList.Children.Remove(row.Root);
                AppList.Children.Insert(index, row.Root);
            }

            if (!row.Hold && app.VolumeWritable)
            {
                _suppress = true;
                row.Slider.Value = Math.Round(app.Volume * 100);
                _suppress = false;
            }
            // 音量读写通道损坏的会话（本机 audioses 问题）：滑块停用不再抢位置，静音仍可用
            row.Slider.IsEnabled = app.VolumeWritable;
            row.Level.Text = app.VolumeWritable
                ? ((int)Math.Round(app.Volume * 100)).ToString()
                : "—";
            row.IconButton.ToolTip = app.Mute ? $"{app.DisplayName}（已静音，点击取消）" : $"{app.DisplayName}（点击静音）";
            UpdateMuteLook(row.MuteX, row.Slider, row.Level, app.Mute);
            index++;
        }

        if (!_warnedBrokenVolume && _audio.Apps.Any(a => !a.VolumeWritable))
        {
            _warnedBrokenVolume = true;
            ToastService.Show("系统音频服务异常：音量调节不可用（静音仍有效），修复方式见日志", 5000);
            Log.Warning("检测到会话音量写入失败（audioses 损坏征兆），相关滑块已停用；静音通道正常");
        }

        foreach (var stale in _rows.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            AppList.Children.Remove(_rows[stale].Root);
            _rows.Remove(stale);
            Log.Debug("混音器移除应用行：{Key}", stale);
        }

        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncMaster()
    {
        DeviceName.Text = _audio.DeviceName; // XAML 里的 "…" 只是占位，读到的设备名在此落地
        // 本机拿不到总音量接口时隐藏整行（不显示假 0），设备名仍保留
        MasterRow.Visibility = _audio.MasterAvailable ? Visibility.Visible : Visibility.Collapsed;
        if (!_audio.MasterAvailable) return;
        _suppress = true;
        MasterSlider.Value = Math.Round(_audio.MasterVolume * 100);
        _suppress = false;
        MasterLevel.Text = ((int)Math.Round(_audio.MasterVolume * 100)).ToString();
        MasterGlyph.Text = SpeakerGlyph(_audio.MasterVolume, _audio.MasterMute);
        MasterIcon.ToolTip = _audio.MasterMute ? "已静音，点击取消" : "静音";
        UpdateMuteLook(null, MasterSlider, MasterLevel, _audio.MasterMute);
    }

    /// <summary>静音视觉：X 覆盖层淡入/淡出（100/150ms，状态过渡强约束）、
    /// 滑杆填充换弱化色、数值转红。masterRow 为 null 表示主音量行（无 X 覆盖）。</summary>
    private static void UpdateMuteLook(UIElement? muteX, Slider slider, TextBlock level, bool muted)
    {
        if (muteX is not null)
        {
            muteX.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(
                muted ? 1 : 0, TimeSpan.FromMilliseconds(muted ? 100 : 150)));
        }
        slider.Foreground = muted
            ? (Brush)slider.FindResource("SliderMutedFillBrush")
            : (Brush)slider.FindResource("NavAccent");
        level.SetResourceReference(TextBlock.ForegroundProperty,
            muted ? "DangerBrush" : "TextSecondary");
    }

    private static string SpeakerGlyph(float volume, bool mute) => mute ? "\uE74F"
        : volume >= 0.67f ? "\uE995"
        : volume >= 0.34f ? "\uE994"
        : volume > 0f ? "\uE993"
        : "\uE992";

    // ---------- 行构建 ----------

    private AppRow BuildRow(MixerApp app)
    {
        var row = new AppRow { Key = app.Key };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Margin = new Thickness(0, 2, 0, 2);

        // 图标按钮：点击静音/取消；图标失败退回（系统声音用喇叭字形，普通应用用首字母）
        var iconButton = new Button
        {
            Width = 28,
            Height = 28,
            MinHeight = 0,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
        };
        var iconHost = new Grid { Width = 24, Height = 24 };
        var icon = AppIcon.Get(app.ExePath);
        if (icon is not null)
        {
            iconHost.Children.Add(new Image
            {
                Source = icon,
                Width = 20,
                Height = 20,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        else
        {
            var fallbackText = app.IsSystem ? "\uE767" : app.DisplayName.FirstOrDefault().ToString().ToUpperInvariant();
            iconHost.Children.Add(app.IsSystem
                ? new TextBlock
                {
                    Text = fallbackText,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 16,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : new Border
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(4),
                    Background = (Brush)FindResource("CardBackground"),
                    BorderBrush = (Brush)FindResource("CardBorderColor"),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = fallbackText,
                        FontSize = (double)FindResource("FontSizeCaption"),
                        Foreground = (Brush)FindResource("TextSecondary"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                });
        }

        // 静音 X 覆盖层（EarTrumpet 式：斜叉盖图标）
        var muteX = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M3,3 L21,21 M21,3 L3,21"),
            Stroke = (Brush)FindResource("DangerBrush"),
            StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 24,
            Height = 24,
            Stretch = Stretch.None,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        iconHost.Children.Add(muteX);
        iconButton.Content = iconHost;
        iconButton.Click += (_, _) => ToggleAppMute(row);
        Grid.SetColumn(iconButton, 0);

        var slider = new Slider
        {
            Margin = new Thickness(8, 0, 8, 0),
            Style = (Style)FindResource("MixerSliderStyle"),
        };
        slider.ValueChanged += (_, e) => OnAppSliderChanged(row, e);
        slider.GotMouseCapture += (_, _) => row.Hold = true;
        slider.LostMouseCapture += (_, _) =>
        {
            if (!row.Hold) return;
            row.Hold = false;
            Log.Debug("应用音量拖动结束：{Key} → {Value}", row.Key, (int)slider.Value);
        };
        slider.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            slider.Value = Math.Clamp(slider.Value + (e.Delta > 0 ? 2 : -2), 0, 100);
        };
        Grid.SetColumn(slider, 1);

        var level = new TextBlock
        {
            MinWidth = 28,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = (double)FindResource("FontSizeBody"),
            Text = "0",
        };
        level.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        Grid.SetColumn(level, 2);

        grid.Children.Add(iconButton);
        grid.Children.Add(slider);
        grid.Children.Add(level);

        row.Root = grid;
        row.IconButton = iconButton;
        row.MuteX = muteX;
        row.Slider = slider;
        row.Level = level;
        return row;
    }

    private sealed class AppRow
    {
        public string Key = "";
        public Grid Root = null!;
        public Button IconButton = null!;
        public UIElement MuteX = null!;
        public Slider Slider = null!;
        public TextBlock Level = null!;
        public bool Hold; // 拖动中：轮询不回写该滑块
    }

    // ---------- 交互 ----------

    private void OnMasterSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        _audio.SetMasterVolume((float)(e.NewValue / 100));
        MasterLevel.Text = ((int)Math.Round(e.NewValue)).ToString();
        MasterGlyph.Text = SpeakerGlyph(_audio.MasterVolume, _audio.MasterMute);
    }

    private void OnMasterIconClick(object sender, RoutedEventArgs e)
    {
        _audio.SetMasterMute(!_audio.MasterMute);
        SyncMaster();
        Log.Debug("主音量静音切换：{Mute}", _audio.MasterMute);
    }

    private void OnAppSliderChanged(AppRow row, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        if (_audio.SetAppVolume(row.Key, (float)(e.NewValue / 100)))
            row.Level.Text = ((int)Math.Round(e.NewValue)).ToString();
    }

    private void ToggleAppMute(AppRow row)
    {
        var app = _audio.Apps.FirstOrDefault(a => a.Key == row.Key);
        if (app is null) return;
        if (!_audio.SetAppMute(row.Key, !app.Mute)) return;
        row.IconButton.ToolTip = app.Mute ? $"{app.DisplayName}（已静音，点击取消）" : $"{app.DisplayName}（点击静音）";
        UpdateMuteLook(row.MuteX, row.Slider, row.Level, app.Mute);
        Log.Debug("应用静音切换：{App} → {Mute}", app.DisplayName, app.Mute);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideAnimated();
        }
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        if (_closing) return;
        if (Environment.TickCount - _shownAt < ActivateGuardMs) return;
        HideAnimated();
    }
}
