using LesTool.Platform;
using LesTool.Services.Plugins;
using Serilog;
namespace LesTool.Services;

/// <summary>Compatibility facade; all entries and execution live in one catalog/executor.</summary>
public static class ActionRegistry
{
    private static readonly CommandCatalog Catalog = new();
    private static readonly CommandExecutor Executor = new(Catalog);
    private static readonly CancellationTokenSource Stopping = new();
    private static readonly HashSet<Task> InFlight = new();
    private static readonly object FlightGate = new();
    public static IReadOnlyList<LesCommand> ForScenario(CommandScenarios scenario) => Catalog.ForScenario(scenario);
    public static LesCommand? Find(string id) => Catalog.Find(id)?.Metadata;
    public static string NameOf(string id) => Find(id)?.Name ?? id;
    public static void Register(string id, string name, string glyph, Action<LesContext> callback,
        CommandKind kind = CommandKind.Builtin, string keywords = "", ContextKind requires = ContextKind.None,
        CommandScenarios scenarios = CommandScenarios.All, ContextKind optional = ContextKind.None) =>
        AddBatch(new[] { new LesCommand(id, name, glyph, keywords, kind, scenarios, requires, optional, callback) });
    internal static void AddBatch(IReadOnlyList<LesCommand> commands, PluginRuntime? owner = null,
        IReadOnlyDictionary<string, Func<LesContext, CancellationToken, Task>>? handlers = null,
        IReadOnlyDictionary<string, CommandConcurrency>? policies = null) => Catalog.AddBatch(commands, owner, handlers, policies);
    internal static void RemoveBatch(IEnumerable<LesCommand> commands) => Catalog.RemoveBatch(commands);
    public static void Execute(string id, TriggerSource source, TriggerSnapshot? snapshot = null) => _ = ObserveAsync(id, source, snapshot);
    private static async Task ObserveAsync(string id, TriggerSource source, TriggerSnapshot? snapshot)
    {
        try { await ExecuteAsync(id, source, snapshot); }
        catch (Exception error) { Log.Error(error, "命令入口失败：{Id}", id); }
    }
    public static async Task<CommandResult> ExecuteAsync(string id, TriggerSource source, TriggerSnapshot? snapshot = null)
    {
        if (Stopping.IsCancellationRequested) return CommandResult.Cancelled;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (FlightGate) InFlight.Add(completion.Task);
        try { return await Executor.ExecuteAsync(id, TriggerEnvelope.Capture(source, snapshot), Stopping.Token); }
        finally
        {
            completion.TrySetResult();
            lock (FlightGate) InFlight.Remove(completion.Task);
        }
    }
    internal static void Stop()
    {
        try { Stopping.Cancel(); } catch (Exception error) { Log.Error(error, "命令取消回调失败"); }
    }
    internal static async Task DrainAsync()
    {
        Task[] tasks; lock (FlightGate) tasks = InFlight.ToArray();
        await Task.WhenAll(tasks);
    }
}
