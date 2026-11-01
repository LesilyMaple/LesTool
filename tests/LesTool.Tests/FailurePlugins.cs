using System.IO;
using System.Windows.Controls;
using LesTool.Platform;
namespace LesTool.Tests;

public abstract class FailurePlugin : ILesPlugin
{
    private string? _marker;
    public abstract string Id { get; }
    protected abstract bool FailDuringStart { get; }
    public void Initialize(ILesHost host)
    {
        _marker = host.Assets.Resolve("marker.txt")!;
        host.OwnCleanup(() => File.AppendAllText(_marker, "scope;"));
        host.RegisterCommand(Id + ".run", "Fixture", "", _ => { });
        host.RegisterSettingsPage(() => new StackPanel());
        if (!FailDuringStart) throw new InvalidOperationException("injected initialization failure");
        ((IPluginLifetimeHost)host).Lifetime.OnStarted(() => throw new InvalidOperationException("injected activation failure"));
    }
    public void Shutdown() => File.AppendAllText(_marker!, "shutdown;");
}
public sealed class InitializeFailurePlugin : FailurePlugin
{ public override string Id => "fixture-initialize"; protected override bool FailDuringStart => false; }
public sealed class StartFailurePlugin : FailurePlugin
{ public override string Id => "fixture-start"; protected override bool FailDuringStart => true; }
