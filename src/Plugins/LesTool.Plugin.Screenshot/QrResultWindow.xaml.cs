using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 二维码识别结果卡：识别完成后由截图服务弹出并收摊遮罩，本窗独立存活，ESC 关闭。
/// 「打开链接」仅对 http/https 显示——其它内容（wifi/vCard/陌生 scheme）只给文本，
/// 不给一键直开入口，防内容里藏的地址被误触发。
/// 定位沿用窗口铁律：Loaded 后纯物理像素运算居中到选区所在显示器。
/// </summary>
internal partial class QrResultWindow : Window
{
    private readonly QrDecoder.QrResult _qr;
    private readonly Rect _selectionPhys;
    private readonly Uri? _openUri;

    internal QrResultWindow(QrDecoder.QrResult qr, Rect selectionPhys)
    {
        InitializeComponent();
        _qr = qr;
        _selectionPhys = selectionPhys;
        QrText.Text = qr.Text;

        _openUri = QrDecoder.IsHttpUrl(qr.Text) ? new Uri(qr.Text.Trim()) : null;
        if (_openUri is null)
            BtnOpen.Visibility = Visibility.Collapsed;
        QrStat.Text = $"{(_openUri is null ? "文本" : "链接")} · {qr.Text.Length} 字符";

        Loaded += OnFirstLoaded;
    }

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        WindowCentering.CenterOnSelectionMonitor(this, _selectionPhys);
        QrText.Focus();
        QrText.SelectAll(); // 高频动作是复制，打开即全选好 Ctrl+C
        Log.Information("二维码结果窗已打开：{Kind}，{Chars} 字符",
            _openUri is null ? "文本" : "链接", _qr.Text.Length);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_qr.Text.Length == 0)
            return;
        try
        {
            Clipboard.SetText(_qr.Text);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "复制二维码内容失败");
            ToastService.Show("复制失败，详见日志", 4000);
            return;
        }
        Log.Information("已复制二维码内容（{Chars} 字符）", _qr.Text.Length);
        ShowCopiedFeedback();
    }

    /// <summary>按钮文字短暂变「已复制」作回执（一次性计时器，窗口已关则不回写）。</summary>
    private void ShowCopiedFeedback()
    {
        string original = (string)BtnCopy.Content;
        BtnCopy.Content = "已复制";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (BtnCopy.IsLoaded)
                BtnCopy.Content = original;
        };
        timer.Start();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        // 纵深防御：临打开再校验一次 scheme（构造期白名单之外的独立关卡）
        if (_openUri?.Scheme is not ("http" or "https"))
            return;
        try
        {
            OpenInDefaultBrowser(_openUri);
            Log.Information("已打开二维码链接：{Url}", _openUri);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开二维码链接失败");
            ToastService.Show("打开链接失败，详见日志", 4000);
        }
    }

    /// <summary>经系统协议处理程序（默认浏览器）打开 URL。
    /// 直接调 ShellExecuteEx 原语：verb="open"、URL 独占 lpFile、无 lpParameters/
    /// lpDirectory——结构上不存在可拼接的命令行，URL 只按协议分发，不进命令解释器。</summary>
    private static void OpenInDefaultBrowser(Uri uri)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            lpVerb = "open",
            lpFile = uri.ToString(),
            nShow = SW_SHOWNORMAL,
        };
        if (!ShellExecuteExW(ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private const int SW_SHOWNORMAL = 1;

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string lpParameters;
        public string lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }
}
