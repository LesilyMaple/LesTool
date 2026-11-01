using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using LesTool.Platform;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Serilog;

namespace LesTool.Services.Scripting;

/// <summary>编译失败（语法/语义错误），Messages 为已格式化的诊断列表（带 .csx 真实行号）。</summary>
public sealed class ScriptCompilationException : Exception
{
    public IReadOnlyList<string> Messages { get; }

    public ScriptCompilationException(IReadOnlyList<string> messages)
        : base($"脚本编译失败，{messages.Count} 条诊断") => Messages = messages;
}

/// <summary>
/// 用户脚本引擎：扫描 %APPDATA%\LesTool\Scripts\*.csx，注册为 script: 前缀动作，
/// 与内置动作同一注册表，热键/轮盘无需感知差别。
///
/// 编译与缓存（两级）：
/// 1. L1 内存：内容指纹 → 编译任务（GetOrAdd 单飞），重复触发零成本；
/// 2. L2 磁盘：%APPDATA%\LesTool\ScriptCache\{脚本名}.{指纹}.dll(+.pdb)，重启后命中即免编译，
///    装载用字节数组不锁文件；指纹 = SHA256(脚本内容 + 宿主身份戳 + 引擎版本)，
///    任一变化整体失配——宿主重编译后旧 IL 自动作废，杜绝调到已删改 API 运行期才炸；
/// 3. 未命中：ScriptSourceGenerator 包装 → CSharpCompilation 编译（引用全部已加载程序集，
///    全能力无白名单）→ 先装载成功再原子落盘，只缓存能装载的产物。
/// 预热（阶段三）：启动数秒后、以及脚本保存（目录监听去抖）后，后台预跑编译管线，
/// 把"改完脚本/重编译宿主后的首次触发"也变成热路径；预热失败只记 DEBUG，触发时才正式上报。
///
/// 隔离策略（脚本出问题不影响其他功能）：
/// 1. 编译与执行都在后台线程，UI 永不阻塞；编译错误只记日志 + Toast；
/// 2. 运行异常逐层捕获，只记日志 + Toast，不外抛到热键派发；
/// 3. 超时（默认 30s）后放弃等待，且该脚本滞留重入闸、不再放行触发，防线程堆积
///    （被放弃的线程仍在跑，只能重启清除；日志会写明）；
/// 4. 已知边界：栈溢出等不可捕获故障在进程内无法防御，个人自用接受。
/// </summary>
public static class ScriptEngine
{
    public static readonly string ScriptsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LesTool", "Scripts");

    public static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LesTool", "ScriptCache");

    private const string IdPrefix = "script:";
    private const string DefaultGlyph = "\uE943"; // Segoe MDL2 Assets "Code"
    private const string EngineVersion = "v1"; // 包装格式变更时递增，强制全量重编
    private const int HashLength = 12;
    private const int MaxDiagnostics = 20;
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WarmupDelay = TimeSpan.FromSeconds(3);

    private sealed class CompiledScript
    {
        public required Func<LesApi, Task> Run { get; init; }
    }

    /// <summary>
    /// L1：指纹 → 编译任务。GetOrAdd 天然单飞——预编译与用户触发同时到达同一脚本只编译一次。
    /// 改一次脚本多一个条目（KB 级），不设上限；失败任务留在缓存，同内容重触发复现同一错误，
    /// 改内容换指纹即自然重编。
    /// </summary>
    private static readonly ConcurrentDictionary<string, Task<CompiledScript>> _compiled = new();

    /// <summary>重入闸：在跑或已超时卡住的脚本路径。超时者滞留至重启（见类注释）。</summary>
    private static readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.OrdinalIgnoreCase);

    private static FileSystemWatcher? _watcher;
    private static IReadOnlyList<MetadataReference>? _references;
    private static string? _hostStamp;

    private static IReadOnlyList<MetadataReference> References => _references ??= BuildReferences();

    /// <summary>启动时调用一次（UI 线程）：建目录、放示例、挂目录监听、注册动作并清扫缓存。</summary>
    public static void Initialize()
    {
        try
        {
            var first = !Directory.Exists(ScriptsDirectory);
            Directory.CreateDirectory(ScriptsDirectory);
            Directory.CreateDirectory(CacheDirectory);
            if (first || Directory.GetFiles(ScriptsDirectory, "*.csx").Length == 0)
                WriteSampleScript();
            WatchScriptsDirectory();
            Rescan();
            WarmPendingScripts(); // 阶段三：宿主重编译后缓存全量失配，空闲时提前重编
        }
        catch (Exception ex)
        {
            Log.Error(ex, "脚本引擎初始化失败，用户脚本不可用");
        }
    }

    /// <summary>
    /// 重扫脚本目录、重注册命令并清扫缓存（UI 线程；ActionRegistry 非线程安全）。
    /// 单个文件失败不影响其余。
    /// </summary>
    public static void Rescan()
    {
        var registered = 0;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(ScriptsDirectory, "*.csx"))
            {
                try
                {
                    var content = File.ReadAllBytes(path);
                    var file = Path.GetFileNameWithoutExtension(path);
                    ParseDirectives(DecodeText(content), out var name, out var glyph);
                    var displayName = string.IsNullOrWhiteSpace(name) ? file : name;
                    ActionRegistry.Register(IdPrefix + file, displayName, glyph,
                        ctx => _ = RunAsync(path),
                        CommandKind.Script,
                        keywords: string.IsNullOrWhiteSpace(name) ? file : $"{name} {file}");
                    registered++;
                    keep.Add($"{file}.{IdentityHash(content)}"); // dll 与 pdb 同名去扩展名
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "注册脚本失败，跳过：{Path}", path);
                }
            }
            SweepCache(keep);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "扫描脚本目录失败：{Dir}", ScriptsDirectory);
        }
        Log.Information("用户脚本扫描完成：{Count} 个已注册", registered);
    }

    private static async Task RunAsync(string path)
    {
        var id = IdPrefix + Path.GetFileNameWithoutExtension(path);
        if (!_running.TryAdd(path, 0))
        {
            Log.Warning("脚本正在运行或已超时停发，忽略本次触发：{Id}", id);
            return;
        }

        var timedOut = false;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            byte[] content;
            try
            {
                content = await File.ReadAllBytesAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "脚本文件读取失败：{Id}（{Path}）", id, path);
                return;
            }

            var hash = IdentityHash(content);
            CompiledScript compiled;
            try
            {
                compiled = await GetOrCreateCompileTask(path, content, hash);
            }
            catch (ScriptCompilationException ex)
            {
                foreach (var message in ex.Messages)
                    Log.Error("脚本编译错误 {Id}：{Message}", id, message);
                ToastService.Show("脚本编译失败，详见日志");
                return;
            }
            catch (Exception ex)
            {
                // 编译管线本身故障（装载/反射阶段），与脚本语法问题分开归类，便于排查
                Log.Error(ex, "脚本编译管线故障（非语法问题）：{Id}（{Path}）", id, path);
                ToastService.Show("脚本装载失败，详见日志");
                return;
            }

            Log.Information("执行用户脚本：{Id}", id);
            await Task.Run(() => compiled.Run(new LesApi())).WaitAsync(RunTimeout);
            Log.Debug("脚本执行完成：{Id}，耗时 {Ms}ms", id, watch.ElapsedMilliseconds);
        }
        catch (TimeoutException)
        {
            timedOut = true; // 线程已放弃但未死：滞留重入闸防连触堆积，重启应用清除
            Log.Error("脚本超时（>{Sec}s），已放弃等待并停发：{Id}", id, RunTimeout.TotalSeconds);
            ToastService.Show("脚本超时，已停用至重启");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "脚本执行失败：{Id}", id);
            ToastService.Show("脚本执行失败，详见日志");
        }
        finally
        {
            if (!timedOut) _running.TryRemove(path, out _);
        }
    }

    /// <summary>
    /// 取指纹对应的编译任务；没有就启动一个（L2 命中则只是装载，不编译）。
    /// GetOrAdd 天然单飞：预编译与用户触发同时到达同一脚本，只编译一次、双方等同一个任务。
    /// </summary>
    private static Task<CompiledScript> GetOrCreateCompileTask(string path, byte[] content, string hash) =>
        _compiled.GetOrAdd(hash, _ => Task.Run(() => LoadFromCacheOrCompile(path, content, hash)));

    /// <summary>
    /// 阶段三·启动预热：延迟几秒（让启动路径先走完，不与编码器探测等后台任务抢资源）
    /// 后把 L1 未热的脚本全部预跑编译管线。
    /// </summary>
    private static void WarmPendingScripts()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(WarmupDelay);
            PreCompilePending();
        });
    }

    /// <summary>
    /// 阶段三·预热遍历：L1 未热的脚本逐个启动编译任务（L2 命中仅装载）。
    /// 预热失败只记 DEBUG——保存到一半的脚本报编译错是常态，正式上报（ERROR+Toast）留给真实触发；
    /// 但诊断明细照常落日志（带 .csx 行号），保证"保存完就坏"的场景不触发也能在日志里查到原因。
    /// </summary>
    private static void PreCompilePending()
    {
        var warmed = 0;
        var skipped = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(ScriptsDirectory, "*.csx"))
            {
                try
                {
                    var content = File.ReadAllBytes(path);
                    var hash = IdentityHash(content);
                    if (_compiled.ContainsKey(hash))
                    {
                        skipped++;
                        continue;
                    }

                    warmed++;
                    GetOrCreateCompileTask(path, content, hash).ContinueWith(t =>
                    {
                        var error = t.Exception!.GetBaseException();
                        switch (error)
                        {
                            case ScriptCompilationException compileError:
                                foreach (var message in compileError.Messages)
                                    Log.Debug("预编译诊断 {Path}：{Message}", path, message);
                                break;
                            default:
                                Log.Debug("预编译故障 {Path}：{Type}：{Reason}",
                                    path, error.GetType().Name, error.Message);
                                break;
                        }
                    }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
                catch (Exception ex)
                {
                    Log.Debug("预编译跳过：{Path}：{Type}：{Reason}",
                        path, ex.GetType().Name, ex.Message);
                }
            }
            Log.Debug("预热完成：新热 {Warmed} 个，已热跳过 {Skipped} 个", warmed, skipped);
        }
        catch (Exception ex)
        {
            Log.Debug("预编译遍历失败：{Type}：{Reason}", ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>仅后台线程调用：先试磁盘缓存，未命中编译并落盘。</summary>
    private static CompiledScript LoadFromCacheOrCompile(string path, byte[] content, string hash)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dllPath = Path.Combine(CacheDirectory, $"{name}.{hash}.dll");
        var pdbPath = Path.Combine(CacheDirectory, $"{name}.{hash}.pdb");
        try
        {
            if (File.Exists(dllPath))
            {
                var cachedPe = File.ReadAllBytes(dllPath);
                var cachedPdb = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;
                var hit = Load(cachedPe, cachedPdb);
                Log.Debug("IL 缓存命中：{Dll}", dllPath);
                return hit;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "缓存装载失败，回退编译：{Dll}", dllPath);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var wrapped = ScriptSourceGenerator.Generate(path, DecodeText(content));
        var compilation = CSharpCompilation.Create(
            $"LesTool.Script.{name}.{hash}",
            new[] { CSharpSyntaxTree.ParseText(wrapped, new CSharpParseOptions(LanguageVersion.Latest)) },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithAllowUnsafe(true)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        using var peStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var result = compilation.Emit(peStream, pdbStream);
        if (!result.Success)
        {
            var messages = result.Diagnostics.Take(MaxDiagnostics)
                .Select(d => d.ToString()).ToList();
            if (result.Diagnostics.Length > MaxDiagnostics)
                messages.Add($"……共 {result.Diagnostics.Length} 条诊断");
            throw new ScriptCompilationException(messages);
        }

        var pe = peStream.ToArray();
        var pdb = pdbStream.ToArray();
        var compiled = Load(pe, pdb); // 先装载成功再落盘：只缓存能装载的产物
        WriteAtomically(dllPath, pe);
        WriteAtomically(pdbPath, pdb);
        Log.Debug("脚本已编译并落盘：{Dll}（{Ms}ms）", dllPath, watch.ElapsedMilliseconds);
        return compiled;
    }

    /// <summary>字节数组装载（不锁缓存文件），反射取入口一次、存 L1 后续纯委托调用。</summary>
    private static CompiledScript Load(byte[] pe, byte[]? pdb)
    {
        var assembly = pdb is null ? Assembly.Load(pe) : Assembly.Load(pe, pdb);
        var type = assembly.GetType("LesTool.ScriptHost.__Script")
                   ?? throw new InvalidOperationException("包装入口类型缺失");
        var method = type.GetMethod("__Run", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException("包装入口方法缺失");
        return new CompiledScript { Run = method.CreateDelegate<Func<LesApi, Task>>() };
    }

    /// <summary>全部已加载程序集都给脚本（全能力）；再显式拉几个惰加载的常用库，免得 using 找不到。</summary>
    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        var assemblies = new List<Assembly>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                assemblies.Add(assembly);
        assemblies.Add(typeof(System.Text.RegularExpressions.Regex).Assembly);
        assemblies.Add(typeof(System.Diagnostics.Process).Assembly);
        assemblies.Add(typeof(System.Net.Http.HttpClient).Assembly);
        assemblies.Add(typeof(System.Text.Json.JsonSerializer).Assembly);
        assemblies.Add(typeof(Microsoft.Win32.Registry).Assembly); // WPF 默认不加载；系统交互类脚本（注册表等）常用

        var references = new List<MetadataReference>();
        foreach (var assembly in assemblies.Distinct())
        {
            try
            {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
            catch (Exception ex)
            {
                Log.Debug("跳过无法引用的程序集 {Name}：{Reason}", assembly.FullName, ex.Message);
            }
        }
        Log.Debug("脚本编译引用就绪：{Count} 个程序集", references.Count);
        return references;
    }

    /// <summary>宿主身份戳：LesTool.dll 的构建时间+大小。重编译必变 → 全部旧缓存整体失配。</summary>
    private static string HostStamp =>
        _hostStamp ??= SafeHostStamp();

    private static string SafeHostStamp()
    {
        try
        {
            var self = new FileInfo(typeof(ScriptEngine).Assembly.Location);
            return $"{self.LastWriteTimeUtc.Ticks:x16}{self.Length:x}";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>内容+宿主+引擎三要素指纹；任一变化即整体失配，缓存文件名自带指纹。</summary>
    private static string IdentityHash(byte[] content)
    {
        var prefix = Encoding.UTF8.GetBytes(EngineVersion + "|" + HostStamp + "|");
        var input = new byte[prefix.Length + content.Length];
        prefix.CopyTo(input, 0);
        content.CopyTo(input, prefix.Length);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(input))[..HashLength].ToLowerInvariant();
    }

    private static string DecodeText(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content);
        return text.StartsWith('\uFEFF') ? text[1..] : text;
    }

    /// <summary>尽力而为：落盘失败不影响本次运行（L1 已热），重启后重编即可，只记警告。</summary>
    private static void WriteAtomically(string path, byte[] bytes)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "缓存落盘失败（本次运行不受影响）：{Path}", path);
        }
    }

    /// <summary>删掉不在白名单（当前脚本的当前指纹）里的缓存文件；装载走字节不锁文件，随时可删。</summary>
    private static void SweepCache(HashSet<string> keep)
    {
        try
        {
            var removed = 0;
            foreach (var file in Directory.EnumerateFiles(CacheDirectory))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                if (keep.Contains(stem) && !file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (IOException)
                {
                    // 被占用就留给下次
                }
            }
            if (removed > 0) Log.Debug("缓存清扫：删除 {Count} 个过期文件", removed);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "缓存清扫失败（不影响运行）");
        }
    }

    /// <summary>只看文件头连续注释区，解析 // name: 与 // glyph: 指令。</summary>
    private static void ParseDirectives(string code, out string? name, out string glyph)
    {
        name = null;
        glyph = DefaultGlyph;
        foreach (var raw in code.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith("//", StringComparison.Ordinal)) break;

            var comment = line[2..].TrimStart();
            if (name is null && TryDirective(comment, "name:", out var value))
                name = value.Trim();
            else if (TryDirective(comment, "glyph:", out var glyphValue)
                     && TryParseGlyph(glyphValue.Trim(), out var parsed))
                glyph = parsed;
        }
    }

    private static bool TryDirective(string comment, string key, out string value)
    {
        if (comment.StartsWith(key, StringComparison.OrdinalIgnoreCase))
        {
            value = comment[key.Length..];
            return true;
        }
        value = "";
        return false;
    }

    /// <summary>接受 E943 / \uE943 / U+E943 / 0xE943 四种写法。</summary>
    private static bool TryParseGlyph(string value, out string glyph)
    {
        glyph = DefaultGlyph;
        var hex = value.Replace("0x", "", StringComparison.OrdinalIgnoreCase).TrimStart('\\', 'u', 'U', '+');
        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
            || code is <= 0 or > 0xFFFF)
            return false;
        glyph = char.ConvertFromUtf32(code);
        return true;
    }

    /// <summary>新建/改名/保存 .csx 都会来几枪事件，去抖半秒后统一重扫，选单里即时可见。</summary>
    private static void WatchScriptsDirectory()
    {
        try
        {
            _watcher = new FileSystemWatcher(ScriptsDirectory, "*.csx")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };

            var debounce = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            debounce.Tick += (_, _) =>
            {
                debounce.Stop();
                Rescan();
                _ = Task.Run(PreCompilePending); // 阶段三：保存/新增后立即后台预热，首次触发免编译
            };

            // 事件来自线程池线程，DispatcherTimer 只能在其 Dispatcher 线程上操作
            void Bounce() => debounce.Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
            _watcher.Created += (_, _) => Bounce();
            _watcher.Changed += (_, _) => Bounce();
            _watcher.Deleted += (_, _) => Bounce();
            _watcher.Renamed += (_, _) => Bounce();
            // 缓冲溢出等错误会让监听静默失效——不订阅的话新增文件识别不了且无从排查
            _watcher.Error += (_, e) =>
                Log.Warning(e.GetException(), "脚本目录监听出错（可能事件溢出），如新增/修改未生效请重启应用");

            Log.Debug("脚本目录监听就绪：{Dir}", ScriptsDirectory);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "脚本目录监听失败，新增文件需重启后可见：{Dir}", ScriptsDirectory);
        }
    }

    private static void WriteSampleScript()
    {
        var sample = Path.Combine(ScriptsDirectory, "示例.csx");
        if (File.Exists(sample)) return;
        const string content = """
            // name: 示例·鼠标与剪贴板
            // glyph: E943
            // LesTool 用户脚本：在 设置→热键 里把动作「示例·鼠标与剪贴板」绑到快捷键即可触发。
            // 头部指令可改显示名与图标（glyph 为 Segoe MDL2 Assets 码位）。
            // 脚本拥有应用全部能力（已加载程序集全引用），Les 只是少打字的入口：
            //   Les.Mouse / Les.ClipboardText / Les.Toast(msg) / Les.App / Les.Settings
            // 启动程序 / 打开文件或 URL：直接 System.Diagnostics.Process.Start（UseShellExecute=true 即双击语义）
            // 每次触发都重新读本文件——改完保存即生效，无需重启。
            var p = Les.Mouse;
            var clip = Les.ClipboardText;
            Les.Toast($"鼠标 {p.X}, {p.Y}｜剪贴板 {clip?.Length ?? 0} 字");
            """;
        // 带 BOM：避免无 BOM UTF-8 中文被按本地代码页误读
        File.WriteAllText(sample, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Log.Information("已生成示例脚本：{Path}", sample);
    }
}
