namespace LesTool.Platform;

public sealed record CommandDescriptor(string Id, string Name, string Glyph, string Keywords = "",
    CommandScenarios Scenarios = CommandScenarios.All, ContextKind Requires = ContextKind.None, ContextKind Optional = ContextKind.None,
    CommandConcurrency Concurrency = CommandConcurrency.RejectReentry);
public enum CommandConcurrency { Allow, RejectReentry }
public interface IAsyncCommandHost
{
    void RegisterAsyncCommand(CommandDescriptor descriptor, Func<LesContext, CancellationToken, Task> handler);
}
