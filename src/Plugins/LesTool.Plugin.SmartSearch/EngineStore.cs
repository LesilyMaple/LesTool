using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.SmartSearch;

/// <summary>搜索引擎定义。Url 里的 {0} 是搜索词占位（URL 编码后替换）；
/// Prefix 是输入条里的调用前缀（如 "gh wpf" 用 GitHub 搜），可空；
/// IsDefault 全表至多一个（没有则取第一行当默认）。</summary>
public sealed class SearchEngine
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Prefix { get; set; } = ""; // 输入条前缀（空 = 无前缀）；前缀至少 1 字符才可能命中
    public bool IsDefault { get; set; }

    public string BuildUrl(string query) => Url.Replace("{0}", Uri.EscapeDataString(query));
}

/// <summary>自定义识别规则：正则命中 → 用 Url 模板打开。
/// {0} = 命中文本（正则有捕获组时取组1，便于剥掉前缀后缀）。
/// 优先级排在内置识别器之后（2026-10-29 设计拍板：砍掉的内置项转成停用示例放这里）。</summary>
public sealed class CustomRule
{
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 引擎与自定义规则的存取层：IPluginSettings JSON 持久化，首启写预置（引擎 5 条、
/// 规则示例 2 条停用）。表对象常驻共享——设置页改完即时生效，识别管线每次现读，
/// 增删改不需要重启（与"插件文件级增删改重启生效"的架构约定不冲突：那是 dll 层面）。
/// </summary>
public sealed class EngineStore
{
    private const string EnginesKey = "engines";
    private const string RulesKey = "customRules";

    private static readonly List<SearchEngine> PresetEngines = new()
    {
        new() { Name = "必应", Url = "https://www.bing.com/search?q={0}", Prefix = "by", IsDefault = true },
        new() { Name = "百度", Url = "https://www.baidu.com/s?wd={0}", Prefix = "bd" },
        new() { Name = "Google", Url = "https://www.google.com/search?q={0}", Prefix = "g" },
        new() { Name = "GitHub", Url = "https://github.com/search?q={0}", Prefix = "gh" },
        new() { Name = "哔哩哔哩", Url = "https://search.bilibili.com/all?keyword={0}", Prefix = "b" },
    };

    private static readonly List<CustomRule> PresetRules = new()
    {
        new() { Name = "DOI 打开文献", Pattern = @"^(?:https?://(?:dx\.)?doi\.org/)?(10\.\d{4,9}/\S+)$", Url = "https://doi.org/{0}", Enabled = false },
        new() { Name = "快递单号查询", Pattern = @"^((?:SF|YT|ZT|YD|JD|EMS)[0-9]{10,14})$", Url = "https://www.kuaidi100.com/chaxun?nu={0}", Enabled = false },
    };

    private readonly IPluginSettings _settings;

    public List<SearchEngine> Engines { get; private set; } = new();
    public List<CustomRule> Rules { get; private set; } = new();

    public EngineStore(IPluginSettings settings)
    {
        _settings = settings;
        Engines = settings.Get(EnginesKey, new List<SearchEngine>());
        if (Engines.Count == 0)
        {
            Engines = PresetEngines.Select(e => new SearchEngine { Name = e.Name, Url = e.Url, Prefix = e.Prefix, IsDefault = e.IsDefault }).ToList();
            SaveEngines();
        }
        Rules = settings.Get(RulesKey, new List<CustomRule>());
        if (Rules.Count == 0)
        {
            Rules = PresetRules.Select(r => new CustomRule { Name = r.Name, Pattern = r.Pattern, Url = r.Url, Enabled = r.Enabled }).ToList();
            SaveRules();
        }
        Log.Information("智能搜索配置已载入：引擎 {Engines} 条（默认 {Default}），自定义规则 {Rules} 条",
            Engines.Count, DefaultEngine?.Name ?? "无", Rules.Count);
    }

    public SearchEngine? DefaultEngine =>
        Engines.FirstOrDefault(e => e.IsDefault) ?? Engines.FirstOrDefault();

    public SearchEngine? ByPrefix(string prefix) =>
        Engines.FirstOrDefault(e => string.Equals(e.Prefix, prefix, StringComparison.OrdinalIgnoreCase));

    public void SaveEngines()
    {
        _settings.Set(EnginesKey, Engines);
        Log.Information("搜索引擎表已保存：{Count} 条（默认 {Default}）", Engines.Count, DefaultEngine?.Name ?? "无");
    }

    public void SaveRules()
    {
        _settings.Set(RulesKey, Rules);
        var enabled = Rules.Count(r => r.Enabled);
        Log.Information("自定义识别规则已保存：{Count} 条（启用 {Enabled}）", Rules.Count, enabled);
    }
}
