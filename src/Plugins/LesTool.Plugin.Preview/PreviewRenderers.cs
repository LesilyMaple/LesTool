using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace LesTool.Plugin.Preview;

/// <summary>内容元素与其期望 DIP 尺寸（窗口层据此定窗口尺寸与物理定位）。
/// Pause 供持续型内容（GIF 动画/Office 解析任务/系统句柄）在窗口收起/换内容时停摆。</summary>
internal sealed record PreviewContent(FrameworkElement Element, Size DesiredDip, Action? Pause = null);

/// <summary>
/// 预览内容渲染器：按文件类型分派（图片/文本/文件夹/信息卡兜底），返回内容元素
/// 与期望 DIP 尺寸。图片按"1:1 物理像素"报期望尺寸（像素/显示器缩放），
/// 大图经 DecodePixelWidth 预缩到 2560px 解码上限；文本前 2MB / 5000 行截断，
/// 编码 BOM → 严格 UTF-8 → GB18030 兜底（中文 Windows 无 BOM 老文件）。
/// </summary>
internal static class PreviewRenderers
{

    private const int TextMaxBytes = 2 * 1024 * 1024;
    private const int TextMaxLines = 5000;
    private const int ImageDecodeMaxPixels = 2560; // 更大者按此宽度预缩解码，防大图内存峰值
    private const double TextWidthDip = 480;
    private const double TextHeightDip = 560;
    private const double CardWidthDip = 400;

    static PreviewRenderers() =>
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); // GB18030 可用

    /// <summary>
    /// 按类型分派渲染器。Office 类文件三层瀑布：系统句柄（原生）→ NPOI 自绘
    /// （异步填充，swap 由预览窗提供）→ 异常时错误卡；其余类型与原有路径不变。
    /// </summary>
    public static PreviewContent Build(string path, double monitorScale, Action<FrameworkElement>? swap = null)
    {
        try
        {
            if (Directory.Exists(path))
                return Folder(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".gif")
                return AnimatedGif(path, monitorScale);
            if (ImageExts.Contains(ext))
                return Image(path, monitorScale);
            if (VideoExts.Contains(ext))
                return Video(path, monitorScale, swap);
            if (AudioExts.Contains(ext))
                return Audio(path, swap);
            if (TextExts.Contains(ext))
                return Text(path);
            if (ShellPreviewExts.Contains(ext) && ShellPreview.TryCreate(path) is { } shell)
                return shell; // 第一层：系统句柄原生渲染（装了 Office/WPS 即命中）
            if (ext is ".xlsx" or ".xls")
                return BuildOfficeAsync(swap, OfficeDocument.ReadExcel, OfficeViews.BuildExcel, path);
            if (ext is ".docx" or ".doc")
                return BuildOfficeAsync(swap, OfficeDocument.ReadWord, OfficeViews.BuildWord, path);            return InfoCard(path);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "预览内容构建失败：{Path}", path);
            return ErrorCard(path, ex.Message);
        }
    }

    /// <summary>注册表里可能挂了系统句柄的扩展名（发现不了自动落到自绘/信息卡）。</summary>
    private static readonly HashSet<string> ShellPreviewExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".rtf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".docm", ".xlsm",
    };

    /// <summary>
    /// Office 自绘的异步填充：先返回加载占位（窗口立即弹出），后台线程解析数据，
    /// 完成后经 swap 换入真内容并重摆窗口。Pause 取消后台任务（窗口收起即停）。
    /// swap 为空（理论上不发生）退化为同步构建。
    /// </summary>
    private static PreviewContent BuildOfficeAsync<T>(
        Action<FrameworkElement>? swap,
        Func<string, T> read,
        Func<T, FrameworkElement> build,
        string path)
    {
        if (swap is null)
        {
            try
            {
                return new PreviewContent(build(read(path)), OfficeViews.LoadingSizeDip);
            }
            catch (Exception ex)
            {
                return new PreviewContent(OfficeViews.ErrorView(ExMessage(ex)), OfficeViews.LoadingSizeDip);
            }
        }

        var cts = new CancellationTokenSource();
        _ = Task.Run(() => read(path), cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested) return;
            try
            {
                swap(build(t.Result));
                Serilog.Log.Debug("Office 自绘填充完成：{Path}", path);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Office 自绘填充失败：{Path}", path);
                swap(OfficeViews.ErrorView(ExMessage(ex)));
            }
        }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());

        return new PreviewContent(OfficeViews.LoadingCard(), OfficeViews.LoadingSizeDip, cts.Cancel);
    }

    private static string ExMessage(Exception ex) =>
        ex is AggregateException aggregate ? aggregate.GetBaseException().Message : ex.Message;

    // ---------- 图片 ----------

    private static readonly HashSet<string> ImageExts =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".ico", ".tif", ".tiff" };

    private static PreviewContent Image(string path, double monitorScale)
    {
        var bmp = new BitmapImage();
        using (var stream = File.OpenRead(path))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = stream;
            bmp.DecodePixelWidth = ImageDecodeMaxPixels; // 只缩不放：小图不受影响，大图限解码量
            bmp.EndInit();
        }
        bmp.Freeze();

        // 1:1 物理像素：期望 DIP = 像素 ÷ 显示器缩放（与源图 DPI 元数据无关，显式尺寸覆盖）
        double widthDip = bmp.PixelWidth / monitorScale;
        double heightDip = bmp.PixelHeight / monitorScale;
        var image = new Image
        {
            Source = bmp,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Width = widthDip,
            Height = heightDip,
        };
        return new PreviewContent(image, new Size(widthDip, heightDip));
    }

    // ---------- 视频 / 音频 ----------

    /// <summary>系统 WMF 引擎能解的视频容器（mp4/mov 稳；mkv/hevc 取决于系统解码器，
    /// 解不了走 MediaFailed 降级提示——与录屏预览窗同款 MediaElement 纪律）。</summary>
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".ts", ".mpg", ".mpeg", ".3gp",
    };

    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".opus",
    };

    /// <summary>
    /// 视频：MediaElement 自动循环播放（带声），点击画面播放/暂停（窗口不抢焦点，
    /// 点击无需焦点）。占位 480×360 弹窗，MediaOpened 后按媒体分辨率改 1:1 物理尺寸
    /// 并经 swap 重摆窗口。解码失败降级提示卡。Pause=暂停（窗口收起即停）。
    /// </summary>
    private static PreviewContent Video(string path, double monitorScale, Action<FrameworkElement>? swap)
    {
        var media = new MediaElement
        {
            Source = new Uri(Path.GetFullPath(path)),
            LoadedBehavior = MediaState.Manual,   // Manual：必须显式 Play 才加载（录屏预览窗同款教训）
            UnloadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Width = 480,
            Height = 360,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        media.Loaded += (_, _) => media.Play();
        media.MediaOpened += (_, _) =>
        {
            if (media.NaturalVideoWidth > 0 && media.NaturalVideoHeight > 0)
            {
                media.Width = media.NaturalVideoWidth / monitorScale;   // 1:1 物理像素
                media.Height = media.NaturalVideoHeight / monitorScale;
                swap?.Invoke(media); // 同元素重赋值无害，触发窗口按新尺寸重摆
            }
        };
        media.MediaEnded += (_, _) =>
        {
            media.Position = TimeSpan.Zero;
            media.Play(); // 循环
        };
        media.MediaFailed += (_, e) =>
        {
            Serilog.Log.Debug("视频解码失败（{Path}）：{Error}", path, e.ErrorException.Message);
            swap?.Invoke(OfficeViews.ErrorView("此视频系统解不了（缺解码器或编码过新），可装解码包后重试"));
        };
        bool playing = false; // Manual 模式下 MediaElement 无可靠的播放态属性，自维护
        media.MouseLeftButtonUp += (_, _) =>
        {
            if (playing) media.Pause();
            else media.Play();
            playing = !playing;
        };
        return new PreviewContent(media, new Size(480, 360), media.Pause);
    }

    /// <summary>
    /// 音频：播放控制卡（大图标点击播放/暂停、进度条可拖、时间显示），自动循环。
    /// MediaElement 隐藏在卡内专职出声；Pause 停媒体与进度定时器。
    /// </summary>
    private static PreviewContent Audio(string path, Action<FrameworkElement>? swap)
    {
        var media = new MediaElement
        {
            Source = new Uri(Path.GetFullPath(path)),
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            Visibility = Visibility.Collapsed,
            Width = 0,
            Height = 0,
        };

        var app = Application.Current;
        var primary = (System.Windows.Media.Brush)app.FindResource("TextPrimary");
        var secondary = (System.Windows.Media.Brush)app.FindResource("TextSecondary");
        double caption = (double)app.FindResource("FontSizeCaption");

        var time = new TextBlock { Text = "0:00 / 0:00", FontSize = caption, Foreground = secondary };
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Width = 300,
            Focusable = false,
            IsMoveToPointEnabled = true,
        };
        var glyph = new TextBlock
        {
            Text = "\uE767",
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 44,
            Foreground = primary,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var playText = new TextBlock
        {
            Text = "正在播放",
            FontSize = (double)app.FindResource("FontSizeBody"),
            Foreground = secondary,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        bool seeking = false;
        slider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            new System.Windows.Controls.Primitives.DragStartedEventHandler((_, _) => seeking = true));
        slider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) =>
            {
                media.Position = TimeSpan.FromSeconds(slider.Value);
                seeking = false;
            }));

        var ticker = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        ticker.Tick += (_, _) =>
        {
            if (seeking) return;
            if (media.NaturalDuration.HasTimeSpan)
            {
                slider.Maximum = media.NaturalDuration.TimeSpan.TotalSeconds;
                slider.Value = media.Position.TotalSeconds;
                time.Text = $"{FormatClock(media.Position)} / {FormatClock(media.NaturalDuration.TimeSpan)}";
            }
        };

        media.Loaded += (_, _) => media.Play();
        media.MediaOpened += (_, _) =>
        {
            if (media.NaturalDuration.HasTimeSpan)
                time.Text = $"0:00 / {FormatClock(media.NaturalDuration.TimeSpan)}";
        };
        media.MediaEnded += (_, _) =>
        {
            media.Position = TimeSpan.Zero;
            media.Play(); // 循环
        };
        media.MediaFailed += (_, e) =>
        {
            Serilog.Log.Debug("音频解码失败（{Path}）：{Error}", path, e.ErrorException.Message);
            swap?.Invoke(OfficeViews.ErrorView("此音频系统解不了（编码过新或缺解码器）"));
        };

        var card = new StackPanel { Width = 360, Margin = new Thickness(0, 8, 0, 4) };
        card.Children.Add(glyph);
        card.Children.Add(new Border { Height = 12 });
        card.Children.Add(playText);
        card.Children.Add(new Border { Height = 14 });
        card.Children.Add(slider);
        card.Children.Add(time);

        bool playing = false; // Manual 模式下无可靠播放态属性，自维护
        void Toggle()
        {
            if (playing) media.Pause();
            else media.Play();
            playing = !playing;
            playText.Text = playing ? "正在播放" : "已暂停 · 点击图标继续";
        }
        glyph.MouseLeftButtonUp += (_, _) => Toggle();
        media.MouseLeftButtonUp += (_, _) => Toggle();

        ticker.Start();
        return new PreviewContent(card, new Size(360, 240), () =>
        {
            ticker.Stop();
            media.Pause();
        });

        static string FormatClock(TimeSpan t) => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    // ---------- GIF 动图 ----------

    private const int GifMaxFrames = 300;            // 帧数上限：超限只显首帧（防长动画内存峰值）
    private const long GifMaxTotalPixels = 80_000_000; // 全帧总像素上限（约 300MB 解码位图）

    /// <summary>
    /// GIF 动图：GifBitmapDecoder 逐帧解码 + DispatcherTimer 按每帧 Graphic Control
    /// Extension 的延迟循环播放（WIC 对每帧输出完整合成位图，直接切 Source 即可）。
    /// 帧数或总像素超限时降级为静态首帧。Pause 停计时器——预览窗收起/换内容时调，
    /// 窗口复用后重开走全新 Present，无需恢复语义。
    /// </summary>
    private static PreviewContent AnimatedGif(string path, double monitorScale)
    {
        GifBitmapDecoder decoder;
        using (var stream = File.OpenRead(path))
        {
            decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        var frames = decoder.Frames;
        var first = frames[0];

        double widthDip = first.PixelWidth / monitorScale;   // 各帧 canvas 同尺寸，首帧即代表
        double heightDip = first.PixelHeight / monitorScale;
        var image = new Image
        {
            Source = first,
            Stretch = System.Windows.Media.Stretch.Uniform,
            Width = widthDip,
            Height = heightDip,
        };
        var size = new Size(widthDip, heightDip);

        if (frames.Count <= 1) return new PreviewContent(image, size);

        long totalPixels = 0;
        foreach (var frame in frames)
            totalPixels += (long)frame.PixelWidth * frame.PixelHeight;
        if (frames.Count > GifMaxFrames || totalPixels > GifMaxTotalPixels)
        {
            Serilog.Log.Debug("GIF 动画过大降级为首帧：{Frames} 帧 {Pixels} 像素：{Path}",
                frames.Count, totalPixels, path);
            return new PreviewContent(image, size);
        }

        var delays = new int[frames.Count];
        for (var i = 0; i < frames.Count; i++)
            delays[i] = ReadFrameDelay(frames[i]);

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delays[0]) };
        var index = 0;
        timer.Tick += (_, _) =>
        {
            index = (index + 1) % frames.Count;
            image.Source = frames[index];
            timer.Interval = TimeSpan.FromMilliseconds(delays[index]);
        };
        timer.Start();
        Serilog.Log.Debug("GIF 动画开始播放：{Frames} 帧：{Path}", frames.Count, path);
        return new PreviewContent(image, size, timer.Stop);
    }

    /// <summary>读 GIF 帧延迟（毫秒）。Win8+ 的 WIC 直接挂 /Delay，Win7 风格在 /grctlsct/Delay；
    /// 单位 1/100 秒；缺失或过快（&lt;20ms，浏览器同款钳制）按 100ms。</summary>
    private static int ReadFrameDelay(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta)
            {
                ushort? raw = meta.GetQuery("/Delay") as ushort? ?? meta.GetQuery("/grctlsct/Delay") as ushort?;
                int ms = (raw ?? 0) * 10;
                if (ms >= 20) return ms;
            }
        }
        catch
        {
            // 元数据缺失按默认节奏，不挡播放
        }
        return 100;
    }

    // ---------- 文本 ----------

    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".json", ".xml", ".csv", ".tsv", ".ini", ".cfg", ".conf", ".config",
        ".cs", ".csx", ".js", ".ts", ".jsx", ".tsx", ".py", ".java", ".c", ".h", ".cpp", ".hpp", ".cc",
        ".cshtml", ".razor", ".css", ".scss", ".html", ".htm", ".xaml", ".yml", ".yaml",
        ".sh", ".bash", ".zsh", ".bat", ".cmd", ".ps1", ".psm1", ".sql", ".lua", ".rb", ".go", ".rs",
        ".php", ".vue", ".sln", ".csproj", ".props", ".targets",
        ".gitignore", ".gitattributes", ".editorconfig", ".env",
    };

    private static PreviewContent Text(string path)
    {
        var info = new FileInfo(path);
        bool truncated;
        string text;
        using (var stream = File.OpenRead(path))
        {
            int toRead = (int)Math.Min(stream.Length, TextMaxBytes);
            var raw = new byte[toRead];
            stream.ReadExactly(raw);
            truncated = stream.Length > toRead;
            text = DecodeText(raw);
        }

        int lineBreak = 0;
        for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1)) lineBreak++;
        if (lineBreak > TextMaxLines)
        {
            int cut = 0;
            for (int n = 0; n < TextMaxLines; n++) cut = text.IndexOf('\n', cut) + 1;
            text = text[..cut] + $"\r\n…（文件超过 {TextMaxLines} 行，仅显示开头）";
            truncated = true;
        }
        if (truncated)
            text += "\r\n…（文件超过 2 MB，仅显示开头）";

        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = (double)Application.Current.FindResource("FontSizeBody"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextPrimary"),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Width = TextWidthDip,
            Height = TextHeightDip,
        };
        return new PreviewContent(box, new Size(TextWidthDip, TextHeightDip));
    }

    /// <summary>无 BOM 文本解码：BOM 优先 → 严格 UTF-8 → GB18030 兜底。</summary>
    private static string DecodeText(byte[] raw)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GB18030").GetString(raw);
        }
    }

    // ---------- 文件夹 / 信息卡 / 错误卡 ----------

    private static PreviewContent Folder(string path)
    {
        int entries = 0;
        try
        {
            foreach (var _ in Directory.EnumerateFileSystemEntries(path))
            {
                entries++;
                if (entries >= 2000) break; // 上限防超大目录卡 UI
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "目录条目枚举失败：{Path}", path);
        }
        var info = new DirectoryInfo(path);
        return Card(path,
            ($"条目", entries >= 2000 ? "2000+" : entries.ToString()),
            ("修改时间", FormatTime(info.LastWriteTime)),
            ("路径", TrimToWidth(path)));
    }

    private static PreviewContent InfoCard(string path)
    {
        var info = new FileInfo(path);
        string type = TypeLabel(path);
        return Card(path,
            ("类型", type),
            ("大小", info.Exists ? FormatBytes(info.Length) : "—"),
            ("修改时间", FormatTime(info.LastWriteTime)),
            ("路径", TrimToWidth(path)));
    }

    private static PreviewContent ErrorCard(string path, string message) =>
        Card(path, ("预览失败", message));

    /// <summary>行式信息卡：标签-值两列。全部纯文本，不取系统图标。</summary>
    private static PreviewContent Card(string path, params (string Label, string Value)[] rows)
    {
        var app = Application.Current;
        double fontSize = (double)app.FindResource("FontSizeBody");
        double captionSize = (double)app.FindResource("FontSizeCaption");
        var primary = (System.Windows.Media.Brush)app.FindResource("TextPrimary");
        var secondary = (System.Windows.Media.Brush)app.FindResource("TextSecondary");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Place(grid, new TextBlock
            {
                Text = rows[i].Label,
                FontSize = captionSize,
                Foreground = secondary,
                Margin = new Thickness(0, i == 0 ? 0 : 10, 16, 0),
                VerticalAlignment = VerticalAlignment.Top,
            }, i, 0);
            Place(grid, new TextBlock
            {
                Text = rows[i].Value,
                FontSize = fontSize,
                Foreground = primary,
                Margin = new Thickness(0, i == 0 ? 0 : 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            }, i, 1);
        }

        var host = new Grid { Width = CardWidthDip };
        host.Children.Add(grid);
        grid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double height = grid.DesiredSize.Height;
        return new PreviewContent(host, new Size(CardWidthDip, height));
    }

    // ---------- 公共元信息（窗口头栏使用） ----------

    private static void Place(Grid grid, UIElement child, int row, int column)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    /// <summary>类型描述：常用扩展名给出人话，未知给出"EXT 文件"。</summary>
    internal static string TypeLabel(string path)
    {
        if (Directory.Exists(path)) return "文件夹";
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "PNG 图片",
            ".jpg" or ".jpeg" => "JPEG 图片",
            ".bmp" => "BMP 图片",
            ".gif" => "GIF 动图",
            ".ico" => "图标",
            ".tif" or ".tiff" => "TIFF 图片",
            ".txt" => "纯文本",
            ".md" => "Markdown 文档",
            ".pdf" => "PDF 文档",
            ".docx" or ".docm" => "Word 文档",
            ".doc" => "Word 文档（老格式）",
            ".xlsx" or ".xlsm" => "Excel 工作簿",
            ".xls" => "Excel 工作簿（老格式）",
            ".pptx" or ".ppt" => "PowerPoint 演示",
            ".rtf" => "RTF 富文本",
            ".zip" or ".rar" or ".7z" => "压缩包",
            ".exe" => "应用程序",
            ".dll" => "程序集",
            ".cs" => "C# 源码",
            ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".flv" or ".wmv" => "视频",
            ".mp3" or ".flac" or ".wav" or ".m4a" or ".ogg" or ".wma" or ".aac" => "音频",
            "" => "文件",
            _ => ext.TrimStart('.').ToUpperInvariant() + " 文件",
        };
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 0 => "—",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };

    internal static string FormatTime(DateTime time) => time.ToString("yyyy-MM-dd HH:mm");

    private static string TrimToWidth(string path) =>
        path.Length <= 72 ? path : path[..69] + "…";
}
