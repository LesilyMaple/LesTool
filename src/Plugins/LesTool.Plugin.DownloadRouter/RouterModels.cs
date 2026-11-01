namespace LesTool.Plugin.DownloadRouter;

/// <summary>
/// 分发规则：条件之间是"填了才要求、填了都须满足"（AND）；列表顺序即优先级，先命中先得。
/// 扩展名/关键字都留空 = 认领一切（用户自建的兜底规则，默认不提供）。
/// </summary>
public sealed record RouteRule(
    string Name,
    bool Enabled,
    string Extensions,   // 逗号分隔，带不带点均可，如 "zip,7z,rar"
    string NameContains, // 文件名包含（不区分大小写），留空 = 不限
    string TargetDir);   // 目标文件夹绝对路径，搬运时不存在自动建

/// <summary>启动补扫档位。存设置文件用字符串（Prompt/Off/Auto），别动顺序语义。</summary>
public enum SweepMode
{
    /// <summary>不看也不提。</summary>
    Off,
    /// <summary>有可归档候选时 Toast 告知数量，不动手；确认动作 = 手动执行「立即整理下载」。</summary>
    Prompt,
    /// <summary>启动即自动补扫归类。</summary>
    Auto,
}

/// <summary>账本结果。重名自动加后缀算 Moved（To 字段记最终落点）。</summary>
public enum MoveOutcome
{
    Moved,
    Failed, // 重试穷尽仍失败，源文件原地保留
}

/// <summary>搬运来源：哨兵自动 / 启动补扫 / 手动整理。</summary>
public enum MoveVia
{
    Auto,
    Startup,
    Manual,
}

/// <summary>账本条目：新条目在前，封顶 500 条（MoveLedger 管）。</summary>
public sealed record LedgerEntry(
    DateTime Time,
    string FileName,
    string From,
    string To,
    string Rule,
    MoveOutcome Outcome,
    MoveVia Via,
    string? Detail); // 失败原因等补充

/// <summary>哨兵运行参数快照：UI 改参数 = 整体换引用，后台只读。</summary>
public sealed record SentinelConfig(string WatchFolder, int DebounceSeconds, IReadOnlySet<string> FastLaneExts)
{
    public static SentinelConfig Parse(string folder, int debounceSeconds, string fastLaneRaw)
    {
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (fastLaneRaw ?? "").Split(',', ';', '，'))
        {
            var e = raw.Trim().TrimStart('.').ToLowerInvariant();
            if (e.Length > 0) exts.Add(e);
        }
        return new SentinelConfig(folder, Math.Clamp(debounceSeconds, 1, 300), exts);
    }
}
