using LesTool.Platform;
using LesTool.Services.Plugins;

namespace LesTool.Services;

internal sealed record CommandEntry(LesCommand Metadata, Func<LesContext, CancellationToken, Task> Handler, PluginRuntime? Owner, CommandConcurrency Concurrency)
{
    internal int InFlight;
    internal bool IsActive => Owner is null || Owner.IsActive;
}

internal sealed class CommandCatalog
{
    private readonly Dictionary<string, CommandEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<LesCommand> ForScenario(CommandScenarios scenario) => _entries.Values
        .Where(e => e.IsActive && e.Metadata.Scenarios.HasFlag(scenario)
            && ScenarioCapabilities.Provides(scenario, e.Metadata.Requires)).Select(e => e.Metadata).ToList();
    internal CommandEntry? Find(string id) => _entries.TryGetValue(id, out var e) && e.IsActive ? e : null;
    internal void AddBatch(IReadOnlyList<LesCommand> commands, PluginRuntime? owner = null,
        IReadOnlyDictionary<string, Func<LesContext, CancellationToken, Task>>? handlers = null,
        IReadOnlyDictionary<string, CommandConcurrency>? policies = null)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
        {
            if (!ids.Add(command.Id) || _entries.ContainsKey(command.Id)) throw new InvalidOperationException("命令 Id 重复：" + command.Id);
            foreach (var scenario in new[] { CommandScenarios.Hotkey, CommandScenarios.Palette, CommandScenarios.Radial })
                if (command.Scenarios.HasFlag(scenario) && !ScenarioCapabilities.Provides(scenario, command.Requires))
                    throw new InvalidOperationException("命令场景与必填上下文矛盾：" + command.Id);
        }
        foreach (var command in commands)
        {
            Func<LesContext, CancellationToken, Task> handler = handlers is not null && handlers.TryGetValue(command.Id, out var asyncHandler)
                ? asyncHandler : (context, _) => { command.Callback(context); return Task.CompletedTask; };
            var policy = policies is not null && policies.TryGetValue(command.Id, out var configured) ? configured : CommandConcurrency.Allow;
            _entries.Add(command.Id, new(command, handler, owner, policy));
        }
    }
    internal void RemoveBatch(IEnumerable<LesCommand> commands)
    {
        foreach (var command in commands)
            if (_entries.TryGetValue(command.Id, out var entry) && ReferenceEquals(entry.Metadata, command)) _entries.Remove(command.Id);
    }
}
