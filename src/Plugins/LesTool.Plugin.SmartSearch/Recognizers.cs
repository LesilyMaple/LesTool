using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.SmartSearch;

/// <summary>一个可执行候选：标题 + 说明 + 图标 + 执行闭包。识别器产出，候选浮窗展示。</summary>
public sealed record SmartCandidate(string Title, string Detail, string Glyph, Action Execute);

/// <summary>
/// 识别管线：文本进、候选列表出。内置识别器各自独立开关（设置键 recognizer.{key}，
/// 默认全开），互不排他——一段文字同时命中几个就给几个候选，唯一时直办、多个时浮窗选。
/// 全都不中由默认引擎兜底，识别永不落空（引擎表为空除外）。
/// </summary>
public sealed class RecognizerPipeline
{
    // ---------- 网址 ----------
    // 整段即网址（可无协议头），或从长文本里抠第一个 http(s)/www 链接（排除尾部标点）
    private static readonly Regex WholeUrlRegex = new(
        @"^(?:https?://)?(?:[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\.)+[A-Za-z]{2,13}(?::\d{1,5})?(?:/[!-~]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmbeddedUrlRegex =
        new(@"(?:https?://|www\.)[!-~]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ---------- 本机路径 ----------
    private static readonly Regex PathRegex =
        new(@"^(?:[A-Za-z]:\\|\\\\)[^<>:""|?*\r\n]+$", RegexOptions.Compiled);

    // ---------- 算式 ----------
    private static readonly Regex MathRegex = new(@"^[\d\s+\-*/().,]+$", RegexOptions.Compiled);

    // ---------- 网盘链接 + 提取码 ----------
    private static readonly Regex NetDiskLinkRegex = new(
        @"(?:https?://)?(?:[\w-]+\.)*(?:pan\.baidu\.com|weiyun\.com|lanzou[a-z]{0,2}\.com|alipan\.com|aliyundrive\.com|123pan\.com|pan\.quark\.cn|cloud\.189\.cn|pan\.xunlei\.com)(?:/[^\s]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExtractCodeRegex =
        new(@"(?:提取码|密码|访问码|pwd)[=:#：\s]*([0-9A-Za-z]{4,8})", RegexOptions.Compiled);

    // ---------- B 站视频号 / 邮箱 ----------
    private static readonly Regex BvRegex =
        new(@"(?<![0-9A-Za-z])BV[0-9A-Za-z]{10}(?![0-9A-Za-z])", RegexOptions.Compiled);
    private static readonly Regex AvRegex =
        new(@"(?<![0-9A-Za-z])[aA][vV]([1-9][0-9]{4,})(?![0-9A-Za-z])", RegexOptions.Compiled);
    private static readonly Regex EmailRegex =
        new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+", RegexOptions.Compiled);

    private readonly EngineStore _store;
    private readonly IPluginSettings _settings;

    // 输入条前缀路由（如 "gh wpf"）：前缀 1-8 位字母数字 + 空格 + 搜索词
    private static readonly Regex PrefixRegex = new(@"^([A-Za-z][A-Za-z0-9]{0,7})\s+(\S.*)$", RegexOptions.Compiled);

    public RecognizerPipeline(EngineStore store, IPluginSettings settings)
    {
        _store = store;
        _settings = settings;
    }

    /// <summary>输入条前缀路由：前缀命中引擎时返回单条候选（调用方直办，不出列表）。</summary>
    public SmartCandidate? TryPrefixRoute(string word)
    {
        var match = PrefixRegex.Match(word);
        if (!match.Success) return null;
        if (_store.ByPrefix(match.Groups[1].Value) is not { } engine) return null;
        var term = match.Groups[2].Value.Trim();
        Log.Information("智能搜索：前缀 {Prefix} 命中引擎 {Engine}", match.Groups[1].Value, engine.Name);
        return new SmartCandidate($"用 {engine.Name} 搜索", term, "\uE721",
            () => Executor.OpenUrl(engine.BuildUrl(term)));
    }

    /// <summary>结尾空格时的引擎挑选列表（设计的"选项搜索"，零新增 UI）。</summary>
    public List<SmartCandidate> BuildEngineChooser(string word) =>
        _store.Engines
            .Select(e => new SmartCandidate($"用 {e.Name} 搜索", $"『{word}』", "\uE721",
                () => Executor.OpenUrl(e.BuildUrl(word))))
            .ToList();

    /// <summary>识别主入口：按优先级聚合全部命中（内置 10-60、自定义 70），零命中走默认引擎。</summary>
    public List<SmartCandidate> Recognize(string text)
    {
        var hits = new List<(int Order, SmartCandidate Candidate)>();
        void Add(string key, int order, IEnumerable<SmartCandidate>? candidates)
        {
            if (candidates is null) return;
            var list = candidates.ToList();
            if (list.Count == 0) return;
            if (!_settings.Get($"recognizer.{key}", true))
            {
                Log.Debug("识别器 {Key} 命中 {Count} 条但已停用", key, list.Count);
                return;
            }
            foreach (var candidate in list)
                hits.Add((order, candidate));
        }

        Add("url", 10, RecognizeUrl(text));
        Add("path", 20, RecognizePath(text));
        Add("math", 30, RecognizeMath(text));
        Add("netdisk", 40, RecognizeNetDisk(text));
        Add("bv", 50, RecognizeBilibili(text));
        Add("email", 60, RecognizeEmail(text));
        Add("custom", 70, RecognizeCustomRules(text));

        var result = hits.OrderBy(h => h.Order).Select(h => h.Candidate).ToList();
        if (result.Count > 0)
        {
            Log.Information("智能搜索：命中 {Count} 条候选（{Titles}）",
                result.Count, string.Join("、", result.Select(c => c.Title)));
            return result;
        }

        var engine = _store.DefaultEngine;
        if (engine is null)
        {
            Log.Warning("智能搜索：无候选且引擎表为空（未识别文本：{Preview}）", Preview(text));
            return result;
        }
        result.Add(new SmartCandidate($"用 {engine.Name} 搜索", text, "\uE721",
            () => Executor.OpenUrl(engine.BuildUrl(text))));
        Log.Information("智能搜索：未识别，走默认引擎 {Engine}", engine.Name);
        return result;
    }

    private static string Preview(string text) =>
        text.Length > 50 ? text[..50] + "…" : text;

    private static IEnumerable<SmartCandidate>? RecognizeUrl(string text)
    {
        string? url = null;
        if (WholeUrlRegex.IsMatch(text))
            url = text;
        else if (EmbeddedUrlRegex.Match(text) is { Success: true } embedded)
            url = embedded.Value.TrimEnd('.', ',', ';', ':', '!', ')'); // 句尾标点不是链接的一部分

        if (url is null) return null;
        var open = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : "https://" + url;
        return new[] { new SmartCandidate("打开网址", open, "\uE774", () => Executor.OpenUrl(open)) };
    }

    private static IEnumerable<SmartCandidate>? RecognizePath(string text)
    {
        // 聊天窗口复制的路径常带引号，剥掉再验；实际存在才认（设计拍板：误伤成本高于收益）
        var candidate = text.Trim().Trim('"', '“', '”', '「', '」');
        if (!PathRegex.IsMatch(candidate)) return null;
        var target = Environment.ExpandEnvironmentVariables(candidate);

        var list = new List<SmartCandidate>();
        if (File.Exists(target))
        {
            list.Add(new SmartCandidate("打开文件", target, "\uE8A5", () => Executor.OpenPath(target)));
            list.Add(new SmartCandidate("所在文件夹", Path.GetDirectoryName(target) ?? target, "\uE8B7",
                () => Executor.RevealFile(target)));
        }
        else if (Directory.Exists(target))
        {
            list.Add(new SmartCandidate("打开文件夹", target, "\uE8B7", () => Executor.OpenPath(target)));
        }
        else
        {
            return null;
        }
        return list;
    }

    private static IEnumerable<SmartCandidate>? RecognizeMath(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Length > 200 || !MathRegex.IsMatch(t)) return null;
        if (!t.Any(char.IsDigit) || !t.Any(c => c is '+' or '-' or '*' or '/' or '(' or ')'))
            return null; // 纯数字不当算式（交给搜索引擎当号码查）

        try
        {
            var value = new System.Data.DataTable().Compute(t.Replace(",", ""), null);
            var result = value is double d
                ? Math.Round(d, 8).ToString("0.########", CultureInfo.InvariantCulture)
                : Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(result)) return null;
            return new[] { new SmartCandidate($"{t} = {result}", "点击复制结果", "\uE8EF",
                () => Executor.CopyText(result, $"已复制：{result}")) };
        }
        catch
        {
            return null; // DataTable 不认的式子：不是算式，交给别人
        }
    }

    private static IEnumerable<SmartCandidate>? RecognizeNetDisk(string text)
    {
        var link = NetDiskLinkRegex.Match(text);
        if (!link.Success) return null;
        var open = link.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? link.Value
            : "https://" + link.Value;

        var code = ExtractCodeRegex.Match(text).Groups[1].Value;
        if (code.Length > 0)
        {
            return new[] { new SmartCandidate("打开网盘并复制提取码", $"{link.Value}（{code}）", "\uE753",
                () => { Executor.CopyText(code, $"提取码 {code} 已复制"); Executor.OpenUrl(open); }) };
        }
        return new[] { new SmartCandidate("打开网盘链接", link.Value, "\uE753", () => Executor.OpenUrl(open)) };
    }

    private static IEnumerable<SmartCandidate>? RecognizeBilibili(string text)
    {
        var bv = BvRegex.Match(text);
        if (bv.Success)
        {
            var open = $"https://www.bilibili.com/video/{bv.Value}";
            return new[] { new SmartCandidate("打开B站视频", bv.Value, "\uE768", () => Executor.OpenUrl(open)) };
        }
        var av = AvRegex.Match(text);
        if (av.Success)
        {
            var id = "av" + av.Groups[1].Value;
            var open = $"https://www.bilibili.com/video/{id}";
            return new[] { new SmartCandidate("打开B站视频", id, "\uE768", () => Executor.OpenUrl(open)) };
        }
        return null;
    }

    private static IEnumerable<SmartCandidate>? RecognizeEmail(string text)
    {
        var mail = EmailRegex.Match(text);
        if (!mail.Success) return null;
        var address = mail.Value;
        return new[]
        {
            new SmartCandidate("复制邮箱", address, "\uE8C8", () => Executor.CopyText(address, $"已复制：{address}")),
            new SmartCandidate("发邮件", $"mailto:{address}", "\uE715", () => Executor.OpenUrl($"mailto:{address}")),
        };
    }

    private IEnumerable<SmartCandidate>? RecognizeCustomRules(string text)
    {
        var hits = new List<SmartCandidate>();
        foreach (var rule in _store.Rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Pattern) || string.IsNullOrWhiteSpace(rule.Url))
                continue;
            try
            {
                var regex = new Regex(rule.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(500));
                var match = regex.Match(text);
                if (!match.Success) continue;
                var token = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
                var url = rule.Url.Replace("{0}", Uri.EscapeDataString(token));
                hits.Add(new SmartCandidate(rule.Name, match.Value, "\uE721", () => Executor.OpenUrl(url)));
                Log.Debug("自定义规则命中：{Name}", rule.Name);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "自定义规则 {Name} 正则无效或超时，跳过", rule.Name);
            }
        }
        return hits.Count > 0 ? hits : null;
    }
}

/// <summary>
/// 候选执行器：所有动作的唯一出口（打开/复制），失败 ERROR 日志 + Toast，绝不抛出。
/// 打开动作全部走白名单校验：网址只允许 http(s)/mailto 协议（防"看着像网址的字符串"
/// 实为别的协议处理器），本机路径必须实际存在；定位文件走 Shell32 的
/// SHOpenFolderAndSelectItems，打开动作走 ShellExecuteEx——均不拼命令行参数
/// （Mimosa 2026-10-29 审计定案：识别文本属外部输入，出口一律走 API 不走命令行拼接）。
/// </summary>
internal static class Executor
{
    private const int ClipboardRetryMax = 3;

    private static readonly HashSet<string> AllowedUrlSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto",
    };

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !AllowedUrlSchemes.Contains(uri.Scheme))
        {
            Log.Error("拒绝打开非法网址（协议不在白名单）：{Url}", Preview(url));
            ToastService.Show("网址不合法，未打开", 3000);
            return;
        }
        StartShell(url, "打开网址");
    }

    public static void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Log.Error("拒绝打开不存在的路径：{Path}", path);
            ToastService.Show("路径不存在", 3000);
            return;
        }
        StartShell(path, "打开文件");
    }

    /// <summary>资源管理器里定位文件（父文件夹打开并选中）。Shell32 PIDL 方式，无命令行拼接。</summary>
    public static void RevealFile(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Log.Error("拒绝定位不存在的路径：{Path}", path);
            return;
        }
        try
        {
            int hr = RevealNative(path);
            if (hr != 0)
                throw new ExternalException("SHOpenFolderAndSelectItems 失败", hr);
            Log.Information("已定位文件：{Path}", path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "定位文件失败：{Path}", path);
            ToastService.Show("定位文件失败", 3000);
        }
    }

    private static int RevealNative(string path)
    {
        nint pidl = ILCreateFromPathW(path);
        if (pidl == 0) return unchecked((int)0x80070002); // FILE_NOT_FOUND
        try
        {
            return SHOpenFolderAndSelectItems(pidl, 0, 0, 0);
        }
        finally
        {
            ILFree(pidl);
        }
    }

    /// <summary>复制文本 + Toast。带自发写标记（剪贴板历史不记这条）与抢锁小重试。</summary>
    public static void CopyText(string text, string toast)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ClipboardService.MarkSelfWrite();
                System.Windows.Clipboard.SetText(text);
                ToastService.Show(toast, 2000);
                Log.Information("已复制：{Preview}", Preview(text));
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= ClipboardRetryMax)
                {
                    ClipboardService.ClearSelfWrite();
                    Log.Error(ex, "复制失败（重试 {N} 次）", attempt);
                    ToastService.Show("复制失败", 3000);
                    return;
                }
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>双击语义打开（URL 走默认浏览器、文件走默认程序）。调用前必须完成白名单校验。</summary>
    private static void StartShell(string target, string verb)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_FLAG_NO_UI, // 出错不弹系统对话框，错误由我们 Toast
            lpFile = target,
            nShow = SW_SHOWNORMAL,
        };
        if (ShellExecuteEx(ref info))
        {
            Log.Information("{Verb}：{Target}", verb, target);
        }
        else
        {
            Log.Error("{Verb}失败（Win32 {Error}）：{Target}", verb, Marshal.GetLastWin32Error(), target);
            ToastService.Show($"{verb}失败，详见日志", 3000);
        }
    }

    private static string Preview(string text) => text.Length > 50 ? text[..50] + "…" : text;

    // ---------- Shell32（资源管理器定位 / 双击语义打开） ----------

    private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    private const int SW_SHOWNORMAL = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public nint hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        public string? lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIcon;
        public nint hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SHOpenFolderAndSelectItems(nint pidlFolder, uint cidl, nint apidl, uint dwFlags);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void ILFree(nint pidl);
}
