using System.IO;
using Serilog;

namespace LesTool.Plugin.DownloadRouter;

/// <summary>
/// 规则匹配器：持不可变编译快照，UI 改规则 = 整体换引用（<see cref="SwapRules"/>），
/// 巡检线程只读不加锁。无匹配返回 null——调用方一律不动文件（宁可漏搬不可错搬）。
/// </summary>
public sealed class RouterEngine
{
    private sealed record Compiled(RouteRule Rule, HashSet<string> Exts, string Contains);

    private volatile List<Compiled> _rules = new();

    public void SwapRules(IReadOnlyList<RouteRule> rules)
    {
        var compiled = new List<Compiled>(rules.Count);
        foreach (var r in rules.Where(r => r.Enabled))
        {
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in (r.Extensions ?? "").Split(',', ';', '，'))
            {
                var e = raw.Trim().TrimStart('.').ToLowerInvariant();
                if (e.Length > 0) exts.Add(e);
            }
            compiled.Add(new Compiled(r, exts, (r.NameContains ?? "").Trim()));
        }
        _rules = compiled;
        Log.Debug("规则快照已换：{Total} 条，启用 {On} 条", rules.Count, compiled.Count);
    }

    /// <summary>按快照顺序匹配文件名；扩展名/关键字填了才要求，两者都填 = 同时满足。</summary>
    public RouteRule? TryMatch(string fileName)
    {
        var ext = Path.GetExtension(fileName.AsSpan()).TrimStart('.').ToString();
        foreach (var r in _rules)
        {
            bool extOk = r.Exts.Count == 0 || (ext.Length > 0 && r.Exts.Contains(ext));
            bool nameOk = r.Contains.Length == 0
                          || fileName.Contains(r.Contains, StringComparison.OrdinalIgnoreCase);
            if (extOk && nameOk) return r.Rule;
        }
        return null;
    }
}
