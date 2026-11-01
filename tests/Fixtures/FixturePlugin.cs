using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using LesTool.Platform;
using LesTool.TestDependency;
namespace IsolationFixture;
public sealed class Entry : ILesPlugin
{
#if FIXTURE_A
    public string Id => "isolation-a";
#else
    public string Id => "isolation-b";
#endif
    public string ReadDependency() => Feature.Value;
    public string? ReadResource() => new ResourceManager("IsolationFixture.Messages", typeof(Entry).Assembly).GetString("Hello", CultureInfo.GetCultureInfo("zh-Hans"));
    public FrameworkElement CreateView() => new StackPanel();
    public void Initialize(ILesHost host) => host.RegisterCommand(Id + ".run", ReadDependency(), "", _ => { });
    public void Shutdown() { }
}
