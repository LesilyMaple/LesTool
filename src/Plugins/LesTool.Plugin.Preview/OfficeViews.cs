using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LesTool.Plugin.Preview;

/// <summary>
/// Office 自绘视图（三层瀑布第二层）：网格画笔（Excel 单元格与 Word 表格共用）+
/// 文档画笔（Word 段落流）。颜色尊重文档原色，但与主题底色对比过差的墨色
/// 自动落回主题前景（深浅主题都可读，散光舒适约束）。全部 UI 线程构建，
/// 数据来自 OfficeDocument 的后台解析结果。
/// </summary>
internal static class OfficeViews
{
    internal static readonly Size LoadingSizeDip = new(480, 560);

    // ---------- 占位 / 错误 ----------

    internal static FrameworkElement LoadingCard()
    {
        return new Border
        {
            Width = LoadingSizeDip.Width,
            Height = LoadingSizeDip.Height,
            Child = Centered(new TextBlock
            {
                Text = "正在解析…",
                FontSize = (double)App.Res("FontSizeBody"),
                Foreground = (Brush)App.Res("TextSecondary"),
            }),
        };
    }

    internal static FrameworkElement ErrorView(string message) => new Border
    {
        Width = LoadingSizeDip.Width,
        Height = 320,
        Child = Centered(new TextBlock
        {
            Text = "无法预览：" + message,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 360,
            FontSize = (double)App.Res("FontSizeBody"),
            Foreground = (Brush)App.Res("TextSecondary"),
        }),
    };

    // ---------- Excel ----------

    internal static FrameworkElement BuildExcel(OfficeDocument.ExcelWorkbook workbook)
    {
        var panel = new DockPanel();
        var sheets = workbook.Sheets;
        var contentHolder = new ContentControl();

        // Sheet 切换条（自绘按钮：不赌主题对 ListBox 的覆盖面）
        if (sheets.Count > 1)
        {
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            for (int i = 0; i < sheets.Count; i++)
            {
                int index = i;
                var tab = new Button
                {
                    Content = sheets[i].Name,
                    FontSize = (double)App.Res("FontSizeCaption"),
                    MinHeight = 26,
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(0, 0, 6, 0),
                    Tag = i,
                };
                tab.Click += (_, _) =>
                {
                    contentHolder.Content = BuildSheetGrid(sheets[index]);
                    foreach (Button b in tabs.Children.OfType<Button>())
                        b.Opacity = Equals(b.Tag, index) ? 1.0 : 0.55;
                };
                tab.Opacity = i == 0 ? 1.0 : 0.55;
                tabs.Children.Add(tab);
            }
            DockPanel.SetDock(tabs, Dock.Top);
            panel.Children.Add(tabs);
        }

        contentHolder.Content = BuildSheetGrid(sheets[0]);
        panel.Children.Add(contentHolder);
        return panel;
    }

    private static FrameworkElement BuildSheetGrid(OfficeDocument.ExcelSheet sheet)
    {
        if (sheet.Cells.Count == 0)
        {
            return new TextBlock
            {
                Text = $"「{sheet.Name}」为空",
                FontSize = (double)App.Res("FontSizeBody"),
                Foreground = (Brush)App.Res("TextHint"),
                Margin = new Thickness(0, 12, 0, 12),
            };
        }

        var grid = new Grid();
        foreach (double width in sheet.ColWidthsDip)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Pixel) });
        foreach (double height in sheet.RowHeightsDip)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height, GridUnitType.Pixel) });

        Brush lineBrush = (Brush)App.Res("WindowBorderColor");
        Brush ink = (Brush)App.Res("TextPrimary");
        bool themeDark = IsDarkPanel();
        double fontSize = (double)App.Res("FontSizeCaption");

        foreach (OfficeDocument.ExcelCell cell in sheet.Cells)
        {
            var block = new TextBlock
            {
                Text = cell.Text,
                FontSize = fontSize,
                Padding = new Thickness(4, 1, 4, 1),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (cell.Bold) block.FontWeight = FontWeights.SemiBold;
            if (cell.Italic) block.FontStyle = FontStyles.Italic;
            block.Foreground = ResolveInk(cell.FgHex, cell.BgHex is not null, ink, themeDark);
            block.HorizontalAlignment = (cell.Align, LooksNumeric(cell.Text)) switch
            {
                (2, _) => HorizontalAlignment.Center,
                (3, _) => HorizontalAlignment.Right,
                (1, _) => HorizontalAlignment.Left,
                (_, true) => HorizontalAlignment.Right, // General：数字右对齐（Excel 观感）
                _ => HorizontalAlignment.Left,
            };
            if (cell.BgHex is not null && TryBrush(cell.BgHex, out Brush? fill))
                block.Background = fill;

            var border = new Border
            {
                BorderBrush = lineBrush,
                BorderThickness = new Thickness(0, 0, 0.5, 0.5),
                Child = block,
            };
            Grid.SetRow(border, cell.Row);
            Grid.SetColumn(border, cell.Col);
            if (cell.RowSpan > 1) Grid.SetRowSpan(border, cell.RowSpan);
            if (cell.ColSpan > 1) Grid.SetColumnSpan(border, cell.ColSpan);
            grid.Children.Add(border);
        }

        FrameworkElement result = new ScrollViewer
        {
            Content = grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        if (!sheet.Truncated) return result;

        var stack = new StackPanel();
        stack.Children.Add(result);
        stack.Children.Add(new TextBlock
        {
            Text = "…（内容过长，仅显示开头部分）",
            FontSize = (double)App.Res("FontSizeCaption"),
            Foreground = (Brush)App.Res("TextHint"),
            Margin = new Thickness(0, 8, 0, 0),
        });
        return stack;
    }

    // ---------- Word ----------

    internal static FrameworkElement BuildWord(OfficeDocument.WordDocument document)
    {
        var stack = new StackPanel { Width = 560, Margin = new Thickness(2, 0, 2, 0) };
        Brush ink = (Brush)App.Res("TextPrimary");
        bool themeDark = IsDarkPanel();

        if (document.Blocks.Count == 0)
            stack.Children.Add(new TextBlock
            {
                Text = "文档没有可显示的内容",
                FontSize = (double)App.Res("FontSizeBody"),
                Foreground = (Brush)App.Res("TextHint"),
                Margin = new Thickness(0, 12, 0, 12),
            });

        foreach (object block in document.Blocks)
        {
            switch (block)
            {
                case OfficeDocument.WordParagraph para:
                    stack.Children.Add(BuildParagraph(para, ink, themeDark));
                    break;
                case OfficeDocument.WordTable table:
                    stack.Children.Add(BuildWordTable(table, ink));
                    break;
                case OfficeDocument.WordImage image:
                    FrameworkElement? view = BuildImage(image.Bytes);
                    if (view is not null) stack.Children.Add(view);
                    break;
            }
        }

        if (document.Truncated)
            stack.Children.Add(new TextBlock
            {
                Text = "…（文档过长，仅显示开头部分）",
                FontSize = (double)App.Res("FontSizeCaption"),
                Foreground = (Brush)App.Res("TextHint"),
                Margin = new Thickness(0, 8, 0, 0),
            });

        return new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 640,
        };
    }

    private static FrameworkElement BuildParagraph(OfficeDocument.WordParagraph para, Brush ink, bool themeDark)
    {
        if (para.Runs.Count == 0)
            return new Border { Height = 8 }; // 空段：空行距

        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 3),
            TextAlignment = para.Align switch
            {
                2 => TextAlignment.Center,
                3 => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
        };
        foreach (OfficeDocument.WordRun run in para.Runs)
        {
            double size = Math.Clamp(para.Heading ? run.SizePt * 1.35 : run.SizePt, 9, 28) * 96.0 / 72.0;
            var inline = new Run
            {
                Text = run.Text,
                FontSize = size,
                FontWeight = run.Bold || para.Heading ? FontWeights.SemiBold : FontWeights.Normal,
                FontStyle = run.Italic ? FontStyles.Italic : FontStyles.Normal,
                Foreground = ResolveInk(run.FgHex, false, ink, themeDark),
            };
            block.Inlines.Add(inline);
        }
        return block;
    }

    private static FrameworkElement BuildWordTable(OfficeDocument.WordTable table, Brush ink)
    {
        var grid = new Grid();
        double colWidth = Math.Clamp(540.0 / table.Cols, 60, 240);
        for (int c = 0; c < table.Cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(colWidth, GridUnitType.Pixel) });
        for (int r = 0; r < table.Rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Brush lineBrush = (Brush)App.Res("WindowBorderColor");
        double fontSize = (double)App.Res("FontSizeCaption");
        foreach (OfficeDocument.WordCell cell in table.Cells)
        {
            var border = new Border
            {
                BorderBrush = lineBrush,
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(4, 2, 4, 2),
                Child = new TextBlock
                {
                    Text = cell.Text,
                    FontSize = fontSize,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ink,
                    FontWeight = cell.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                },
            };
            Grid.SetRow(border, cell.Row);
            Grid.SetColumn(border, cell.Col);
            grid.Children.Add(border);
        }

        return new Border
        {
            Margin = new Thickness(0, 6, 0, 6),
            Child = grid,
        };
    }

    private static FrameworkElement? BuildImage(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return new Image
            {
                Source = image,
                MaxWidth = 520,
                MaxHeight = 420,
                Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 4),
            };
        }
        catch (Exception ex)
        {
            // EMF/WMF 等矢量图 WPF 不支持：跳过显示（已知降级）
            Serilog.Log.Debug(ex, "Word 内嵌图片解码失败，跳过");
            return null;
        }
    }

    // ---------- 颜色纪律 ----------

    /// <summary>文档墨色兜底：未指定墨色用主题前景；指定了但与所在底色对比过差
    /// （深底暗字/浅底亮字）时落回主题前景，保住深浅主题下都可读。</summary>
    private static Brush ResolveInk(string? fgHex, bool hasOwnBackground, Brush fallback, bool themeDark)
    {
        if (fgHex is null || !TryBrush(fgHex, out Brush? brush) || brush is not SolidColorBrush solid)
            return fallback;
        double luminance = Luminance(solid.Color);
        if (hasOwnBackground)
            return solid; // 自带底色的字按文档原样（配色成对出现）
        if (themeDark && luminance < 0.30) return fallback;   // 深色面板上的暗字
        if (!themeDark && luminance > 0.78) return fallback;  // 浅色面板上的亮字
        return solid;
    }

    private static bool IsDarkPanel()
    {
        return App.Res("PanelBackground") is SolidColorBrush panel && Luminance(panel.Color) < 0.35;
    }

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private static bool TryBrush(string hex, out Brush? brush)
    {
        try
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            return true;
        }
        catch
        {
            brush = null;
            return false;
        }
    }

    private static bool LooksNumeric(string text) =>
        text.Length > 0 && char.IsDigit(text[0]) || text.StartsWith('-') || text.StartsWith('+');

    private static FrameworkElement Centered(UIElement child) => new Border { Child = child };

    internal static class App
    {
        internal static object Res(string key) => System.Windows.Application.Current.FindResource(key);
    }
}
