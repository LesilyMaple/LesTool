using System.IO;
using System.Text.Json;
using LesTool.Platform;
using LesTool.Plugin.DownloadRouter;
using LesTool.Platform.Storage;
using LesTool.Services;
using LesTool.Services.Plugins;

namespace LesTool.Tests;

internal static class Program
{
    private static int _passed;
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "LesTool.Tests", Guid.NewGuid().ToString("N"));
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--render-recovery") { RecoveryVisualCheck.Render(args[1]); return 0; }
        Directory.CreateDirectory(TestRoot);
        AppPaths.DataRoot = Path.Combine(TestRoot, "app-data");
        try
        {
            Run("settings: new/replace/backup", () =>
            {
                var store = Store("basic");
                Check(store.ReadState == SettingsReadState.NewFile || store.Read().Count == 0);
                store.Save(new() { ["value"] = "old", ["unknown"] = "keep" });
                store.Save(new() { ["value"] = "new", ["unknown"] = "keep" });
                Check(Store("basic").Read()["value"] == "new");
                Check(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(store.FilePath + ".bak"))!["value"] == "old");
            });
            Run("settings: corrupt primary recovers without destroying good backup", () =>
            {
                var store = Store("recover"); store.Save(new() { ["value"] = "good" }); store.Save(new() { ["value"] = "later" });
                File.WriteAllText(store.FilePath, "{");
                var recovered = Store("recover"); Check(recovered.Read()["value"] == "good");
                Check(recovered.ReadState == SettingsReadState.Recovered);
                Check(File.ReadAllText(store.FilePath + ".bak").Contains("good"));
                Check(Directory.GetFiles(TestRoot, "recover.json.corrupt-*").Length == 1);
            });
            Run("settings: corrupt primary and backup protect original", () =>
            {
                var store = Store("protected"); File.WriteAllText(store.FilePath, "broken");
                Check(store.Read().Count == 0 && store.IsProtected);
                Throws<SettingsWriteException>(() => store.Save(new() { ["value"] = "replacement" }));
                Check(File.ReadAllText(store.FilePath) == "broken");
            });
            Run("settings: newer schema is protected even with valid backup", () =>
            {
                var file = Path.Combine(TestRoot, "future.json");
                File.WriteAllText(file, "{\"SchemaVersion\":99}"); File.WriteAllText(file + ".bak", "{}");
                var store = new JsonSettingsStore<Dictionary<string, JsonElement>>(file, () => new());
                store.Read(); Check(store.IsProtected && File.ReadAllText(file).Contains("99"));
            });
            Run("settings: locked destination keeps old version and dirty state", () =>
            {
                var store = Store("locked"); store.Save(new() { ["value"] = "old" });
                using (var held = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    Throws<SettingsWriteException>(() => store.Save(new() { ["value"] = "new" }));
                Check(store.IsDirty && Store("locked").Read()["value"] == "old");
                Check(store.FlushAsync().GetAwaiter().GetResult().Succeeded);
                Check(!store.IsDirty && Store("locked").Read()["value"] == "new");
            });
            Run("settings: async writes cannot resurrect an older snapshot", () =>
            {
                var store = Store("concurrent"); var tasks = new List<Task<SaveResult>>();
                for (var n = 0; n < 30; n++) tasks.Add(store.SaveAsync(new() { ["value"] = n.ToString() }));
                Task.WhenAll(tasks).GetAwaiter().GetResult(); store.FlushAsync().GetAwaiter().GetResult();
                Check(Store("concurrent").Read()["value"] == "29" && !store.IsDirty);
            });
            Run("hotkeys: launcher default is persisted once and clearing survives restart", () =>
            {
                var settings = new AppSettingsData { LauncherHotkeyInitialized = false };
                Check(LauncherHotkeyDefaults.InitializeOnce(settings) && settings.Hotkeys.Count == 1);
                settings.Hotkeys.Clear();
                var restored = JsonSerializer.Deserialize<AppSettingsData>(JsonSerializer.Serialize(settings))!;
                Check(!LauncherHotkeyDefaults.InitializeOnce(restored) && restored.Hotkeys.Count == 0);
                Check(!LauncherHotkeyDefaults.InitializeOnce(new AppSettingsData()));
            });
            Run("hotkeys: missing commands preserve desired bindings", () =>
            {
                var settings = new AppSettingsData { Hotkeys = new() { new() { Action = "missing.command", Keys = "Ctrl+K" } } };
                var before = JsonSerializer.Serialize(settings);
                Check(HotkeyBindingResolver.UnavailableState(settings.Hotkeys[0]) == HotkeyRegState.Unavailable);
                Check(JsonSerializer.Serialize(settings) == before);
                Check(HotkeyBindingResolver.SameKeys("ctrl+alt+k", "Alt+Ctrl+K"));
            });
            Run("commands: duplicate batch commits nothing", () =>
            {
                var existing = Command("test.existing"); ActionRegistry.AddBatch(new[] { existing });
                try
                {
                    Throws<InvalidOperationException>(() => ActionRegistry.AddBatch(new[] { Command("test.new"), Command("TEST.EXISTING") }));
                    Check(ActionRegistry.Find("test.new") is null && ReferenceEquals(ActionRegistry.Find("test.existing"), existing));
                }
                finally { ActionRegistry.RemoveBatch(new[] { existing }); }
            });
            Run("lifetime: reverse cleanup continues after failure and is idempotent", () =>
            {
                var order = new List<int>(); var runtime = new PluginRuntime("test");
                runtime.Own(new Cleanup(() => order.Add(1)));
                runtime.Own(new Cleanup(() => { order.Add(2); throw new Exception("injected"); }));
                runtime.Own(new Cleanup(() => order.Add(3)));
                runtime.Activate(); runtime.Dispose(); runtime.Dispose();
                Check(!runtime.IsActive && runtime.Stopping.IsCancellationRequested && order.SequenceEqual(new[] { 3, 2, 1 }));
            });
            Run("plugins: commands stay private until commit and callbacks respect stopping", () =>
            {
                var context = new PluginHost.PluginHostContext("fixture", TestRoot); var count = 0;
                context.RegisterCommand("fixture.run", "Fixture", "", _ => count++);
                Check(ActionRegistry.Find("fixture.run") is null);
                var data = new LesContext(null, null, null, default, null, 0, null, TriggerSource.Hotkey);
                context.Commands[0].Callback(data); Check(count == 0);
                context.Runtime.Activate(); context.Commands[0].Callback(data); Check(count == 1);
                context.Runtime.Dispose(); context.Commands[0].Callback(data); Check(count == 1);
                Throws<InvalidOperationException>(() => context.RegisterCommand("host.override", "bad", "", _ => { }));
            });
            Run("assets: sibling-prefix traversal is rejected", () =>
            {
                var assets = Path.Combine(TestRoot, "assets"); var sibling = Path.Combine(TestRoot, "assets-other");
                Directory.CreateDirectory(assets); Directory.CreateDirectory(sibling);
                File.WriteAllText(Path.Combine(sibling, "secret.txt"), "test");
                Check(new PluginAssets(assets).Resolve("../assets-other/secret.txt") is null);
            });
            Run("UIA: timed out provider has bounded admission and recovers on return", () =>
            {
                using var worker = new BoundedQueryExecutor(2);
                using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                var first = worker.RunAsync(() => { started.Set(); release.Wait(); return 1; }, TimeSpan.FromMilliseconds(60));
                Check(started.Wait(1000));
                Check(first.GetAwaiter().GetResult().State == QueryState.TimedOut);
                Check(worker.RunAsync(() => 2, TimeSpan.FromMilliseconds(60)).GetAwaiter().GetResult().State == QueryState.Busy);
                release.Set();
                QueryResult<int>? recovered = null;
                for (var n = 0; n < 100; n++)
                {
                    recovered = worker.RunAsync(() => 3, TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult();
                    if (recovered.State == QueryState.Ready) break;
                    Thread.Sleep(5);
                }
                Check(recovered?.State == QueryState.Ready && recovered.Value == 3);
            });
            Run("commands: execution boundary enforces source policy", () =>
            {
                var catalog = new CommandCatalog(); var invoked = false;
                var command = new LesCommand("policy.run", "Policy", "", "", CommandKind.Plugin, CommandScenarios.Hotkey,
                    ContextKind.None, ContextKind.None, _ => invoked = true);
                catalog.AddBatch(new[] { command });
                var executor = new CommandExecutor(catalog, (_, trigger, _) => Task.FromResult(
                    new LesContext(null, null, null, default, null, 0, null, trigger.Source)));
                var result = executor.ExecuteAsync("policy.run", new(Guid.NewGuid(), TriggerSource.CommandPalette, default, default, DateTime.UtcNow)).GetAwaiter().GetResult();
                Check(result == CommandResult.Unavailable && !invoked);
            });
            Run("commands: a stopping owner cannot execute after context completes", () =>
            {
                var catalog = new CommandCatalog(); var owner = new PluginRuntime("cancel"); owner.Activate(); var invoked = false;
                catalog.AddBatch(new[] { new LesCommand("cancel.run", "Cancel", "", "", CommandKind.Plugin, CommandScenarios.All,
                    ContextKind.None, ContextKind.None, _ => invoked = true) }, owner);
                var context = new TaskCompletionSource<LesContext>(TaskCreationOptions.RunContinuationsAsynchronously);
                var executor = new CommandExecutor(catalog, (_, _, _) => context.Task);
                var task = executor.ExecuteAsync("cancel.run", new(Guid.NewGuid(), TriggerSource.Hotkey, default, default, DateTime.UtcNow));
                owner.Quiesce(); context.SetResult(new(null, null, null, default, null, 0, null, TriggerSource.Hotkey));
                Check(task.GetAwaiter().GetResult() == CommandResult.Cancelled && !invoked);
                owner.Dispose();
            });
            Run("settings: explicit reset archives original and releases protection", () =>
            {
                var store = Store("reset"); File.WriteAllText(store.FilePath, "corrupt");
                store.Read(); store.ResetToDefaults(); store.Save(new() { ["value"] = "saved" });
                Check(!store.IsProtected && Store("reset").Read()["value"] == "saved");
                Check(File.ReadAllText(Directory.GetFiles(TestRoot, "reset.json.before-reset-*").Single()) == "corrupt");
            });
            Run("settings: malformed nested scene is protected", () =>
            {
                var file = Path.Combine(AppPaths.DataRoot, "settings.json"); Directory.CreateDirectory(AppPaths.DataRoot);
                File.WriteAllText(file, "{\"Radial\":{\"Scenes\":[null]}}");
                var settings = AppSettings.Load(); Check(AppSettings.IsProtected && !AppSettings.IsNewConfiguration);
                Throws<SettingsWriteException>(() => AppSettings.Save(settings));
                Check(File.ReadAllText(file).Contains("null"));
            });
            Run("UIA: dispose completes active and queued waiters", () =>
            {
                using var worker = new BoundedQueryExecutor(2);
                using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                var active = worker.RunAsync(() => { started.Set(); release.Wait(); return 1; }, Timeout.InfiniteTimeSpan);
                Check(started.Wait(1000));
                var queued = worker.RunAsync(() => 2, Timeout.InfiniteTimeSpan);
                worker.Dispose();
                Check(active.Wait(500) && queued.Wait(500));
                Check(active.Result.State == QueryState.Cancelled && queued.Result.State == QueryState.Cancelled);
                release.Set();
            });
            Run("lifetime: stopping hooks precede final release exactly once", () =>
            {
                var order = new List<int>(); var owner = new PluginRuntime("stop");
                owner.OnStopping(() => order.Add(1)); owner.Own(new Cleanup(() => order.Add(2)));
                owner.Quiesce(); Check(order.SequenceEqual(new[] { 1 })); owner.Quiesce(); owner.Dispose();
                Check(order.SequenceEqual(new[] { 1, 2 }));
            });
            Run("clipboard: unknown private formats forbid partial snapshot", () =>
            {
                Check(SelectionText.CanSnapshotFormats(new[] { "UnicodeText", "HTML Format" }));
                Check(!SelectionText.CanSnapshotFormats(new[] { "UnicodeText", "Biff12" }));
            });
            Run("dependencies: content conflicts reject both owners before load", () =>
            {
                var left = Path.Combine(TestRoot, "plugin-a"); var right = Path.Combine(TestRoot, "plugin-b");
                Directory.CreateDirectory(left); Directory.CreateDirectory(right);
                File.WriteAllBytes(Path.Combine(left, "native.dll"), new byte[] { 1, 2, 3 });
                File.WriteAllBytes(Path.Combine(right, "native.dll"), new byte[] { 1, 2, 4 });
                var errors = PluginDependencyAudit.Check(new[] { left, right }); Check(errors.Count == 2);
            });
            Run("plugins: private versions, shared WPF, satellites and missing dependency isolation", () => PluginIsolationCheck.Run(TestRoot));
            Run("dependencies: stale nested RID output is not part of the active dependency graph", () =>
            {
                var owner = Path.Combine(TestRoot, "stale-build"); Directory.CreateDirectory(Path.Combine(owner, "win-x64"));
                File.WriteAllBytes(Path.Combine(owner, "native.dll"), new byte[] { 1, 2, 3 });
                File.WriteAllBytes(Path.Combine(owner, "win-x64", "native.dll"), new byte[] { 1, 2, 4 });
                Check(PluginDependencyAudit.Check(new[] { owner }).Count == 0);
            });
            Run("commands: optional selection is filtered for palette", () =>
            {
                var catalog = new CommandCatalog(); var invoked = false;
                catalog.AddBatch(new[] { new LesCommand("optional.run", "Optional", "", "", CommandKind.Plugin,
                    CommandScenarios.All, ContextKind.None, ContextKind.SelectedText, _ => invoked = true) });
                var executor = new CommandExecutor(catalog, (kind, trigger, _) =>
                {
                    Check(!kind.HasFlag(ContextKind.SelectedText));
                    return Task.FromResult(new LesContext(null, null, null, default, null, 0, null, trigger.Source));
                });
                var result = executor.ExecuteAsync("optional.run", new(Guid.NewGuid(), TriggerSource.CommandPalette, default, default, DateTime.UtcNow)).GetAwaiter().GetResult();
                Check(result == CommandResult.Succeeded && invoked);
            });
            Run("plugins: real loader rolls back initialization and activation failures", () =>
            {
                foreach (var entry in new[] { ("fixture-initialize", typeof(InitializeFailurePlugin)), ("fixture-start", typeof(StartFailurePlugin)) })
                {
                    var directory = Path.Combine(TestRoot, entry.Item1); var assets = Path.Combine(directory, "assets");
                    Directory.CreateDirectory(assets); var marker = Path.Combine(assets, "marker.txt"); File.WriteAllText(marker, "");
                    var assembly = typeof(Program).Assembly.Location; File.Copy(assembly, Path.Combine(directory, Path.GetFileName(assembly)));
                    File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(new {
                        id = entry.Item1, name = entry.Item1, version = "1.0.0", apiContract = 1, minPlatform = "1.3.0",
                        entryAssembly = Path.GetFileName(assembly), entryType = entry.Item2.FullName }));
                    PluginHost.LoadOne(directory);
                    Check(ActionRegistry.Find(entry.Item1 + ".run") is null && !PluginHost.Pages.Any(p => p.PluginId == entry.Item1));
                    Check(File.ReadAllText(marker) == "shutdown;scope;" && PluginHost.InfoOf(entry.Item1)?.Status == PluginStatus.Failed);
                }
            });
            Run("download: stopping after pause waits for actual move and final ledger", () =>
            {
                var source = Path.Combine(TestRoot, "downloads"); var destination = Path.Combine(TestRoot, "sorted");
                Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(source, "item.txt"), "payload");
                var settings = new MemorySettings(); var ledger = new MoveLedger(settings); var engine = new RouterEngine();
                engine.SwapRules(new[] { new RouteRule("test", true, "txt", "", destination) });
                using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                var sentinel = new DownloadSentinel(engine, ledger, new(source, 1, new HashSet<string> { "txt" }), _ => { },
                    (file, directory) => { entered.Set(); release.Wait(); var target = Path.Combine(directory, Path.GetFileName(file)); File.Move(file, target); return target; });
                try
                {
                    sentinel.Start(); sentinel.Sweep(MoveVia.Manual); Check(entered.Wait(4000));
                    sentinel.Pause(); var stopping = sentinel.StopAsync(); Check(!stopping.Wait(100));
                    release.Set(); Check(stopping.Wait(2000)); ledger.FlushAsync(true).GetAwaiter().GetResult();
                    Check(ledger.CopyForUi().Count == 1 && settings.Get("ledger", new List<LedgerEntry>()).Count == 1);
                }
                finally { release.Set(); sentinel.Dispose(); }
            });
            Run("commands: asynchronous reentry is rejected until completion", () =>
            {
                var catalog = new CommandCatalog(); var end = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var command = Command("busy.run");
                catalog.AddBatch(new[] { command }, handlers: new Dictionary<string, Func<LesContext, CancellationToken, Task>> { [command.Id] = (_, _) => end.Task },
                    policies: new Dictionary<string, CommandConcurrency> { [command.Id] = CommandConcurrency.RejectReentry });
                var executor = new CommandExecutor(catalog, (_, trigger, _) => Task.FromResult(new LesContext(null, null, null, default, null, 0, null, trigger.Source)));
                var trigger = new TriggerEnvelope(Guid.NewGuid(), TriggerSource.Hotkey, default, default, DateTime.UtcNow);
                var first = executor.ExecuteAsync(command.Id, trigger);
                Check(executor.ExecuteAsync(command.Id, trigger).GetAwaiter().GetResult() == CommandResult.Busy);
                end.SetResult(); Check(first.GetAwaiter().GetResult() == CommandResult.Succeeded);
            });
            Run("plugin preferences: case collisions enter protected recovery", () =>
            {
                var file = Path.Combine(AppPaths.DataRoot, "plugins.json");
                File.WriteAllText(file, "{\"IconOverrides\":{\"clipboard\":\"a\",\"CLIPBOARD\":\"b\"}}");
                PluginState.Load(); Check(PluginState.IsProtected && PluginState.IsDisabled("clipboard"));
                PluginState.ResetProtected(); Check(!PluginState.IsProtected);
            });
            Run("shutdown: cancelled command finalizers drain before services stop", () =>
            {
                var finished = false; var command = Command("drain.run");
                ActionRegistry.AddBatch(new[] { command }, handlers: new Dictionary<string, Func<LesContext, CancellationToken, Task>>
                {
                    [command.Id] = async (_, token) =>
                    {
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                        finally { await Task.Delay(100); finished = true; }
                    }
                });
                var executing = ActionRegistry.ExecuteAsync(command.Id, TriggerSource.Hotkey);
                ActionRegistry.Stop(); var draining = ActionRegistry.DrainAsync(); Check(!draining.Wait(25));
                draining.GetAwaiter().GetResult(); Check(finished && executing.GetAwaiter().GetResult() == CommandResult.Cancelled);
                ActionRegistry.RemoveBatch(new[] { command });
            });
            Console.WriteLine($"PASS: {_passed} behavioral checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            // TestRoot is a freshly allocated directory owned only by this test process.
            if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
        }
    }
    private static JsonSettingsStore<Dictionary<string, string>> Store(string name) => new(Path.Combine(TestRoot, name + ".json"), () => new());
    private static LesCommand Command(string id) => new(id, id, "", "", CommandKind.Plugin, CommandScenarios.All, ContextKind.None, ContextKind.None, _ => { });
    private static void Run(string name, Action test) { test(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class MemorySettings : IPluginSettings
    {
        private readonly Dictionary<string, JsonElement> _values = new();
        public T Get<T>(string key, T fallback) { lock (_values) return _values.TryGetValue(key, out var value) ? value.Deserialize<T>()! : fallback; }
        public void Set<T>(string key, T value) { lock (_values) _values[key] = JsonSerializer.SerializeToElement(value); }
    }
    private sealed class Cleanup(Action cleanup) : IDisposable { public void Dispose() => cleanup(); }
}
