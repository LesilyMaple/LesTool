using System.Diagnostics;
using LesTool.Platform;
using Serilog;
namespace LesTool.Services;

public enum CommandResult { Succeeded, Unavailable, MissingContext, Cancelled, Busy, Failed, StaleTrigger }
internal sealed class CommandExecutor(CommandCatalog catalog,
    Func<ContextKind, TriggerEnvelope, CancellationToken, Task<LesContext>>? build = null)
{
    private readonly Func<ContextKind, TriggerEnvelope, CancellationToken, Task<LesContext>> _build = build ?? ContextProvider.BuildAsync;
    internal async Task<CommandResult> ExecuteAsync(string id, TriggerEnvelope trigger, CancellationToken token = default)
    {
        var command = catalog.Find(id);
        if (command is null) return CommandResult.Unavailable;
        var metadata = command.Metadata;
        var scenario = trigger.Source switch { TriggerSource.Hotkey => CommandScenarios.Hotkey,
            TriggerSource.Radial => CommandScenarios.Radial, _ => CommandScenarios.Palette };
        if (!metadata.Scenarios.HasFlag(scenario) || !ScenarioCapabilities.Provides(scenario, metadata.Requires)) return CommandResult.Unavailable;
        var ownsExecution = command.Concurrency == CommandConcurrency.RejectReentry;
        if (ownsExecution && Interlocked.CompareExchange(ref command.InFlight, 1, 0) != 0) return CommandResult.Busy;
        using var linked = command.Owner is null ? CancellationTokenSource.CreateLinkedTokenSource(token)
            : CancellationTokenSource.CreateLinkedTokenSource(token, command.Owner.Stopping);
        var watch = Stopwatch.StartNew();
        try
        {
            var effective = metadata.Requires | metadata.Optional;
            if (!ScenarioCapabilities.Provides(scenario, ContextKind.SelectedText)) effective &= ~ContextKind.SelectedText;
            if (!ScenarioCapabilities.Provides(scenario, ContextKind.SelectedFiles)) effective &= ~ContextKind.SelectedFiles;
            var context = await _build(effective, trigger, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(catalog.Find(id), command)) return CommandResult.Unavailable;
            if (effective.HasFlag(ContextKind.SelectedText) || effective.HasFlag(ContextKind.SelectedFiles))
                if (!trigger.Target.IsForeground || DateTime.UtcNow - trigger.CapturedAt > TimeSpan.FromSeconds(3)) return CommandResult.StaleTrigger;
            if (Missing(metadata.Requires, context) is { } missing)
            { ToastService.Show($"「{metadata.Name}」需要{missing}，当前不可用", 3000); return CommandResult.MissingContext; }
            await command.Handler(context, linked.Token);
            return CommandResult.Succeeded;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return CommandResult.Cancelled; }
        catch (Exception error)
        {
            Log.Error(error, "命令失败：{Id} {Execution}", id, trigger.Id);
            ToastService.Show($"「{metadata.Name}」执行失败，详见日志", 3000);
            return CommandResult.Failed;
        }
        finally { if (ownsExecution) Interlocked.Exchange(ref command.InFlight, 0); Log.Information("命令 {Id} 执行会话 {Execution} 来源 {Source} 耗时 {Ms}ms", id, trigger.Id, trigger.Source, watch.ElapsedMilliseconds); }
    }
    private static string? Missing(ContextKind required, LesContext value)
    {
        if (required.HasFlag(ContextKind.SelectedText) && string.IsNullOrEmpty(value.SelectedText)) return "选中文本";
        if (required.HasFlag(ContextKind.SelectedFiles) && value.SelectedFiles is not { Count: > 0 }) return "选中的文件";
        if (required.HasFlag(ContextKind.Focus) && value.FocusWindowHandle == 0) return "有效目标窗口";
        if (required.HasFlag(ContextKind.Clipboard) && value.ClipboardText is null) return "剪贴板文本";
        return null;
    }
}
