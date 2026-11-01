using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LesTool.Platform;
using LesTool.Services;
using Serilog;

namespace LesTool.Views.Pages;

/// <summary>
/// 轮盘设置页（对齐 Quicker 的三区布局）：
/// 左 = 场景区（筛选、按软件禁用、全局 + 应用场景列表、添加应用）；
/// 右 = 轮盘可视化编辑器（点击扇区绑定命令，几何与悬浮轮盘共用 RadialGeometry，
/// 保证"编辑器里看到的位置 = 实际轮盘上出现的位置"）；
/// 下 = 基础设置（超时收起 + 轮盘尺寸）。
/// 改动即时保存并刷新禁用快照，下次唤出生效。应用场景的槽位三态：
/// 覆盖绑定 / 覆盖为空（该软件下禁用此槽）/ 继承全局（编辑器中灰显）。
/// </summary>
public partial class RadialGesturePage : UserControl
{
    /// <summary>编辑器盘面直径（DIP）。2026-10-17 用户拍板放大 2 倍（300 → 600），
    /// 三圈几何按比例缩放，窗口小时可横向滚动。</summary>
    private const double EditorSize = 600;

    private static readonly int[] TimeoutChoices = { 5, 8, 10, 15, 30 };

    private RadialSceneSettings _scene = null!; // ctor 里立即指向全局场景
    private int _editingSlot;
    private bool _building;

    public RadialGesturePage()
    {
        InitializeComponent();
        App.Settings.Radial.Normalize();
        SelectScene(App.Settings.Radial.Global);
        BuildTimeoutChoices();
        BuildSizeChoices();
    }

    #region 页内 tab

    private void OnBasicTabChecked(object sender, RoutedEventArgs e)
    {
        if (BasicPanel is null) return; // XAML 加载期 IsChecked 触发时尚未就绪
        BasicPanel.Visibility = Visibility.Visible;
        ActionPanel.Visibility = Visibility.Collapsed;
        Log.Debug("轮盘设置页切换到基础设置");
    }

    private void OnActionTabChecked(object sender, RoutedEventArgs e)
    {
        if (ActionPanel is null) return;
        ActionPanel.Visibility = Visibility.Visible;
        BasicPanel.Visibility = Visibility.Collapsed;
        Log.Debug("轮盘设置页切换到动作设置");
    }

    #endregion

    #region 场景区

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_scene is null) return; // XAML 加载期事件早于字段赋值
        BuildSceneList();
    }

    private void OnDisableChanged(object sender, RoutedEventArgs e)
    {
        if (_building || _scene is null) return;
        _scene.Disabled = DisableCheck.IsChecked == true;
        SaveAndSync($"场景 {_scene.Process ?? "全局"} 禁用={_scene.Disabled}");
        BuildSceneList(); // 副标题切换"已禁用轮盘"
    }

    private void BuildSceneList()
    {
        SceneList.Children.Clear();
        SceneList.Children.Add(MakeSceneRow(App.Settings.Radial.Global, "全局场景", "所有软件的默认盘面"));

        var apps = App.Settings.Radial.Scenes.Skip(1).ToList();
        if (apps.Count > 0)
            SceneList.Children.Add(MakeListCaption("应用程序"));

        var filter = FilterBox.Text.Trim();
        foreach (var scene in apps)
        {
            if (filter.Length > 0 && !scene.Process!.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            SceneList.Children.Add(MakeSceneRow(scene, scene.Process!, scene.Disabled ? "已禁用轮盘" : "应用场景"));
        }
    }

    private UIElement MakeSceneRow(RadialSceneSettings scene, string title, string subtitle)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var main = new Button
        {
            Style = (Style)FindResource("CommandItemStyle"),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 13,
                        Foreground = (Brush)TryFindResource("TextPrimary")!,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    new TextBlock
                    {
                        Text = subtitle,
                        FontSize = (double)TryFindResource("FontSizeCaption")!,
                        Foreground = (Brush)TryFindResource("TextHint")!,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 2, 0, 0),
                    },
                },
            },
        };
        if (ReferenceEquals(_scene, scene))
            main.BorderBrush = (Brush)TryFindResource("NavAccent")!;
        main.Click += (_, _) => SelectScene(scene);
        Grid.SetColumn(main, 0);
        row.Children.Add(main);

        if (scene.Process is not null) // 全局场景不可删
        {
            var delete = MakeGhostButton("✕", () => DeleteScene(scene));
            Grid.SetColumn(delete, 1);
            row.Children.Add(delete);
        }
        return row;
    }

    private void SelectScene(RadialSceneSettings scene)
    {
        _scene = scene;
        _building = true;
        DisableCheck.IsChecked = scene.Disabled;
        DisableCheck.Visibility = scene.Process is null ? Visibility.Collapsed : Visibility.Visible;
        _building = false;

        EditorTitle.Text = scene.Process is null ? "全局场景 · 所有软件的默认盘面" : $"{scene.Process} · 应用场景";
        EditorHint.Text = scene.Process is null
            ? "内两圈为盘面扇区；最外虚线圈为扩展圈（实际使用时不可见，划出盘面松开触发）。点击任意扇区绑定命令。"
            : "灰显扇区 = 继承全局；点击扇区可覆盖为本软件专属或恢复继承。扩展圈（最外虚线圈）仅全局场景生效。";
        BuildSceneList();
        BuildWheel();
    }

    private void DeleteScene(RadialSceneSettings scene)
    {
        App.Settings.Radial.Scenes.Remove(scene);
        SaveAndSync($"删除应用场景 {scene.Process}");
        if (ReferenceEquals(_scene, scene))
            SelectScene(App.Settings.Radial.Global);
        else
            BuildSceneList();
    }

    private void OnAddAppClick(object sender, RoutedEventArgs e)
    {
        AppList.Children.Clear();
        var existing = App.Settings.Radial.Scenes
            .Where(s => s.Process != null)
            .Select(s => s.Process!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 对齐任务管理器"应用"区：只列带可见窗口的程序（Edge 这类多进程按应用去重为一条）
        var apps = RadialSceneResolver.EnumerateApps();
        var shown = 0;
        foreach (var app in apps)
        {
            if (existing.Contains(app.Process)) continue;

            var captured = app.Process;
            var item = new Button
            {
                Style = (Style)FindResource("CommandItemStyle"),
                ToolTip = captured, // 场景键 = 进程文件名；展示名认不出时靠悬浮确认
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = app.Display,
                            FontSize = 13,
                            Foreground = (Brush)TryFindResource("TextPrimary")!,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                        new TextBlock
                        {
                            Text = app.Subtitle,
                            FontSize = (double)TryFindResource("FontSizeCaption")!,
                            Foreground = (Brush)TryFindResource("TextHint")!,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Margin = new Thickness(0, 2, 0, 0),
                        },
                    },
                },
            };
            item.Click += (_, _) =>
            {
                AppPopup.IsOpen = false;
                var scene = new RadialSceneSettings { Process = captured };
                App.Settings.Radial.Scenes.Add(scene);
                SaveAndSync($"新增应用场景 {captured}");
                SelectScene(scene);
            };
            AppList.Children.Add(item);
            shown++;
        }

        if (shown == 0)
            AppList.Children.Add(new TextBlock
            {
                Text = "没有可添加的应用：当前没有其他带窗口的运行中程序。",
                FontSize = (double)TryFindResource("FontSizeCaption")!,
                Foreground = (Brush)TryFindResource("TextHint")!,
                Margin = new Thickness(8, 6, 8, 6),
                TextWrapping = TextWrapping.Wrap,
            });
        AppPopup.IsOpen = true;
        Log.Debug("添加应用清单：可列 {Shown} 个（已建场景隐藏 {Hidden} 个）", shown, apps.Count - shown);
    }

    #endregion

    #region 轮盘编辑器

    private void BuildWheel()
    {
        WheelHost.Children.Clear();
        double unit = RadialGeometry.Unit(EditorSize);
        bool isApp = _scene.Process is not null;

        // 底盘只铺到外圈（扩展圈在运行时位于盘外，画在底盘之外用虚线示意）
        double discDiameter = RadialGeometry.EditorSplit2 * 2 * unit;
        var backdrop = new Ellipse
        {
            Width = discDiameter,
            Height = discDiameter,
            Fill = (Brush)TryFindResource("PanelBackground")!,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(backdrop, (EditorSize - discDiameter) / 2);
        Canvas.SetTop(backdrop, (EditorSize - discDiameter) / 2);
        WheelHost.Children.Add(backdrop);

        double innerDiameter = RadialGeometry.EditorCenter * 2 * unit;
        var inner = new Ellipse
        {
            Width = innerDiameter,
            Height = innerDiameter,
            // 与悬浮轮盘一致：底色同扇区，靠亮描边区分（对齐 Quicker）
            Stroke = (Brush)TryFindResource("OverlaySeparatorStrong")!,
            StrokeThickness = 1.5,
            Fill = (Brush)TryFindResource("OverlayHoverBrush")!,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(inner, (EditorSize - innerDiameter) / 2);
        Canvas.SetTop(inner, (EditorSize - innerDiameter) / 2);
        WheelHost.Children.Add(inner);

        var centerLabel = new TextBlock
        {
            Text = _scene.Process ?? "全局",
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = (double)TryFindResource("FontSizeCaption")!,
            Foreground = (Brush)TryFindResource("OverlayTextBrush")!,
            Width = innerDiameter - 6,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(centerLabel, (EditorSize - centerLabel.Width) / 2);
        Canvas.SetTop(centerLabel, EditorSize / 2 - 9);
        WheelHost.Children.Add(centerLabel);

        for (var slot = 0; slot < RadialSettingsData.SlotCount; slot++)
        {
            var index = slot;
            bool isExtension = slot >= RadialSettingsData.ExtensionFirst;
            // 三圈各自的环带与标签半径（编辑器示意比例）
            double innerBase = slot < RadialSettingsData.OuterFirst ? RadialGeometry.EditorCenter
                : slot < RadialSettingsData.ExtensionFirst ? RadialGeometry.EditorSplit1
                : RadialGeometry.EditorSplit2;
            double outerBase = slot < RadialSettingsData.OuterFirst ? RadialGeometry.EditorSplit1
                : slot < RadialSettingsData.ExtensionFirst ? RadialGeometry.EditorSplit2
                : RadialGeometry.EditorOuter;
            double labelBase = (innerBase + outerBase) / 2;

            // 扩展圈仅全局可配；应用场景里画出来但不响应（低暗度 + 提示）
            bool interactive = !isApp || !isExtension;

            var item = _scene.Slots[slot];
            bool inherited = isApp && !item.Override && !isExtension;
            string? actionId = isApp && !isExtension
                ? (item.Override ? item.Action : App.Settings.Radial.Global.Slots[slot].Action)
                : item.Action;

            var sector = new Path
            {
                Data = RadialGeometry.RingSectorPath(EditorSize, innerBase, outerBase, slot % RadialSettingsData.RingSlots),
                Fill = (Brush)TryFindResource("OverlayHoverBrush")!,
                Stroke = (Brush)TryFindResource("OverlaySeparator")!,
                StrokeThickness = 1,
                Opacity = !interactive ? 0.25 : actionId is null ? 0.35 : inherited ? 0.55 : 1,
                Cursor = interactive ? Cursors.Hand : null,
                ToolTip = !interactive ? "扩展圈仅全局场景可配置" : inherited ? "继承全局" : isExtension ? "扩展圈 · 盘外方向盲触发" : null,
            };
            if (isExtension)
                sector.StrokeDashArray = new DoubleCollection { 2, 2 }; // 虚线示意：实际使用时不可见
            if (interactive)
            {
                sector.MouseEnter += (_, _) => sector.Fill = (Brush)TryFindResource("RadialSectorActiveBrush")!;
                sector.MouseLeave += (_, _) => sector.Fill = (Brush)TryFindResource("OverlayHoverBrush")!;
                sector.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    OpenSlotPicker(index);
                };
            }
            WheelHost.Children.Add(sector);

            // 槽位标签（与悬浮轮盘同一摆位规则）；不参与命中，点击都归扇形
            if (ActionRegistry.Find(actionId ?? "") is { } command)
            {
                var label = new StackPanel { IsHitTestVisible = false, Opacity = inherited || !interactive ? 0.65 : 1, ToolTip = command.Name };
                label.Children.Add(new TextBlock
                {
                    Text = command.Glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = (double)TryFindResource("FontSizeBody")!,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)TryFindResource("OverlayIconStroke")!,
                });
                label.Children.Add(new TextBlock
                {
                    Text = command.Name,
                    FontSize = (double)TryFindResource("FontSizeCaption")!,
                    MaxWidth = RadialGeometry.LabelMaxWidthAt(EditorSize, labelBase),
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)TryFindResource("OverlayTextBrush")!,
                });
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var center = RadialGeometry.LabelCenterAt(EditorSize, labelBase, slot % RadialSettingsData.RingSlots);
                Canvas.SetLeft(label, center.X - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, center.Y - label.DesiredSize.Height / 2);
                WheelHost.Children.Add(label);
            }
        }
    }

    private void OpenSlotPicker(int index)
    {
        _editingSlot = index;
        bool isApp = _scene.Process is not null;
        var slot = _scene.Slots[index];
        // 当前生效命令（应用场景继承时显示全局命令为"当前"）
        string? current = isApp && !slot.Override
            ? App.Settings.Radial.Global.Slots[index].Action
            : slot.Action;

        SlotList.Children.Clear();
        foreach (var command in ActionRegistry.ForScenario(CommandScenarios.Radial)) // 按场景声明+能力表过滤
        {
            var id = command.Id;
            var item = new Button
            {
                Style = (Style)FindResource("CommandItemStyle"),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = command.Glyph,
                            FontFamily = new FontFamily("Segoe MDL2 Assets"),
                            FontSize = 14,
                            Foreground = (Brush)TryFindResource("TextSecondary")!,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = command.Name,
                            FontSize = 13,
                            Margin = new Thickness(8, 0, 0, 0),
                            Foreground = (Brush)TryFindResource("TextPrimary")!,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
            };
            if (id == current)
                item.BorderBrush = (Brush)TryFindResource("NavAccent")!;
            item.Click += (_, _) => ApplySlot(id, null);
            SlotList.Children.Add(item);
        }

        if (isApp)
        {
            if (slot.Override)
                AddSlotAction("恢复继承全局", () => ApplySlot(null, true));
            AddSlotAction(slot.Action is null ? "保持清空（此软件下禁用此槽）" : "此软件下清空此槽", () => ApplySlot(null, null));
        }
        else
        {
            AddSlotAction("清空此槽", () => ApplySlot(null, null));
        }

        SlotPopup.IsOpen = true;
    }

    private void AddSlotAction(string text, Action action)
    {
        var item = new Button
        {
            Style = (Style)FindResource("CommandItemStyle"),
            Content = new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = (Brush)TryFindResource("TextHint")!,
            },
        };
        item.Click += (_, _) => action();
        SlotList.Children.Add(item);
    }

    /// <summary>应用槽位改动。actionId=绑定命令；null+inherit=true=恢复继承（仅应用场景）；null=清空。</summary>
    private void ApplySlot(string? actionId, bool? inherit)
    {
        SlotPopup.IsOpen = false;
        var slot = _scene.Slots[_editingSlot];
        if (_scene.Process is null)
        {
            if (slot.Action == actionId) return;
            slot.Action = actionId;
        }
        else if (inherit == true)
        {
            if (!slot.Override) return;
            slot.Action = null;
            slot.Override = false;
        }
        else
        {
            if (slot.Override && slot.Action == actionId) return;
            slot.Action = actionId;
            slot.Override = true;
        }

        SaveAndSync($"轮盘槽 {_editingSlot}（{_scene.Process ?? "全局"}）→ {actionId ?? (inherit == true ? "继承全局" : "空")}");
        BuildWheel();
    }

    #endregion

    #region 基础设置

    private void BuildTimeoutChoices()
    {
        _building = true;
        foreach (var seconds in TimeoutChoices)
        {
            var choice = seconds;
            var radio = new RadioButton
            {
                Content = $"{seconds} 秒",
                Margin = new Thickness(0, 0, 14, 0),
                IsChecked = seconds == App.Settings.Radial.TimeoutSeconds,
            };
            radio.Checked += (_, _) =>
            {
                if (_building) return;
                App.Settings.Radial.TimeoutSeconds = choice;
                SaveAndSync($"轮盘超时改为 {choice}s");
            };
            TimeoutPanel.Children.Add(radio);
        }
        _building = false;
    }

    private void BuildSizeChoices()
    {
        _building = true;
        var labels = new Dictionary<int, string> { { 420, "小" }, { 480, "中" }, { 570, "大" } };
        foreach (var size in RadialSettingsData.AllowedSizes)
        {
            var choice = size;
            var radio = new RadioButton
            {
                Content = labels[choice],
                Margin = new Thickness(0, 0, 14, 0),
                IsChecked = size == App.Settings.Radial.SizeDip,
            };
            radio.Checked += (_, _) =>
            {
                if (_building) return;
                App.Settings.Radial.SizeDip = choice;
                SaveAndSync($"轮盘尺寸改为 {choice} DIP");
            };
            SizePanel.Children.Add(radio);
        }
        _building = false;
    }

    #endregion

    private void SaveAndSync(string message)
    {
        AppSettings.Save(App.Settings);
        RadialSceneResolver.RefreshSnapshot(); // 禁用清单立即反映到钩子线程闸
        Log.Debug("{Message}", message);
    }

    /// <summary>清单小节标题（场景区"应用程序"、添加应用弹窗的分组头共用同一样式）。</summary>
    private TextBlock MakeListCaption(string text) => new()
    {
        Text = text,
        FontSize = (double)TryFindResource("FontSizeCaption")!,
        Foreground = (Brush)TryFindResource("TextHint")!,
        Margin = new Thickness(10, 10, 0, 4),
    };

    /// <summary>幽灵按钮：透明底、hover 才显底色（场景行的删除等次要操作）。</summary>
    private Button MakeGhostButton(string text, Action onClick)
    {
        var button = new Button
        {
            FocusVisualStyle = null,
            Cursor = Cursors.Hand,
            Content = new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = (Brush)TryFindResource("TextSecondary")!,
            },
        };
        button.Click += (_, _) => onClick();

        var template = new ControlTemplate(typeof(Button));
        var factory = new FrameworkElementFactory(typeof(Border));
        factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        factory.SetValue(Border.PaddingProperty, new Thickness(6, 4, 6, 4));
        var hoverSetter = new Trigger { Property = IsMouseOverProperty, Value = true };
        hoverSetter.Setters.Add(new Setter(Border.BackgroundProperty,
            (Brush)TryFindResource("NavHoverBackground")!));
        template.Triggers.Add(hoverSetter);
        factory.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        template.VisualTree = factory;
        button.Template = template;
        return button;
    }
}
