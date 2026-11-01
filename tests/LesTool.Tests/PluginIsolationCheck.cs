using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using LesTool.Platform;
using LesTool.Services.Plugins;
namespace LesTool.Tests;
internal static class PluginIsolationCheck
{
    internal static void Run(string root)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "LesTool.sln"))) repository = repository.Parent;
        if (repository is null) throw new Exception("Cannot locate fixture build outputs");
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var contexts = new List<PluginLoadContext>();
        for (var i = 0; i < 2; i++)
        {
            var letter = i == 0 ? "A" : "B";
            var source = Path.Combine(repository.FullName, "tests", "Fixtures", "Plugin" + letter, "bin", configuration, "net8.0-windows");
            // RID can be inherited by project references in the executable build.
            if (File.Exists(Path.Combine(source, "win-x64", "Plugin" + letter + ".dll"))) source = Path.Combine(source, "win-x64");
            var directory = Path.Combine(root, "isolation-" + letter); Directory.CreateDirectory(directory);
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(directory, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
            }
            var entry = Path.Combine(directory, "Plugin" + letter + ".dll");
            var context = new PluginLoadContext(entry); contexts.Add(context);
            using var reflection = context.EnterContextualReflection();
            var assembly = context.LoadEntry(entry);
            var type = assembly.GetType("IsolationFixture.Entry")!;
            var instance = (ILesPlugin)Activator.CreateInstance(type)!;
            Assert((string)type.GetMethod("ReadDependency")!.Invoke(instance, null)! == "v" + (i + 1));
            Assert(type.GetMethod("CreateView")!.Invoke(instance, null) is FrameworkElement);
            Assert((string)type.GetMethod("ReadResource")!.Invoke(instance, null)! == "中文资源");
            var host = new PluginHost.PluginHostContext(instance.Id, directory, context); instance.Initialize(host);
            Assert(host.Commands.Single().Name == "v" + (i + 1));
            Assert(context.Assemblies.Any(a => a.GetName().Name == "System.LesTool.TestDependency"));
            Assert(!context.Assemblies.Contains(typeof(ILesPlugin).Assembly) && !context.Assemblies.Contains(typeof(FrameworkElement).Assembly));
        }
        Assert(!ReferenceEquals(contexts[0], contexts[1]));
        // A populated default context must not rescue a plugin with a missing private dependency.
        var left = Path.Combine(root, "isolation-A"); var right = Path.Combine(root, "isolation-B");
        Assert(PluginDependencyAudit.Check(new[] { left, right }).Count == 0);
        using (var image = File.OpenRead(Path.Combine(left, "System.LesTool.TestDependency.dll"))) AssemblyLoadContext.Default.LoadFromStream(image);
        var missing = Path.Combine(root, "missing-private"); Directory.CreateDirectory(missing);
        File.Copy(Path.Combine(left, "PluginA.dll"), Path.Combine(missing, "PluginA.dll"));
        File.Copy(Path.Combine(left, "PluginA.deps.json"), Path.Combine(missing, "PluginA.deps.json"));
        var isolated = new PluginLoadContext(Path.Combine(missing, "PluginA.dll"));
        var missingType = isolated.LoadEntry(Path.Combine(missing, "PluginA.dll")).GetType("IsolationFixture.Entry")!;
        try { missingType.GetMethod("ReadDependency")!.Invoke(Activator.CreateInstance(missingType), null); }
        catch (TargetInvocationException e) when (e.InnerException is FileNotFoundException) { return; }
        throw new Exception("Missing private dependency unexpectedly resolved");
    }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Plugin isolation check failed"); }
}
