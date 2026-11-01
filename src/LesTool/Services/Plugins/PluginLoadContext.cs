using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using LesTool.Platform;
using Serilog;

namespace LesTool.Services.Plugins;

/// <summary>One private dependency graph per plugin. WPF plugins remain resident until restart.</summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _directory;
    private readonly HashSet<string> _privateNames = new(StringComparer.OrdinalIgnoreCase);
    internal PluginLoadContext(string entryPath) : base("plugin:" + Path.GetFileName(Path.GetDirectoryName(entryPath)), isCollectible: false)
    {
        _directory = Path.GetDirectoryName(Path.GetFullPath(entryPath))!;
        _resolver = new AssemblyDependencyResolver(Path.GetFullPath(entryPath));
        var manifest = Path.ChangeExtension(entryPath, ".deps.json");
        if (File.Exists(manifest))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (document.RootElement.TryGetProperty("targets", out var targets))
                foreach (var target in targets.EnumerateObject())
                    foreach (var library in target.Value.EnumerateObject())
                        foreach (var group in library.Value.EnumerateObject().Where(p => p.Name is "runtime" or "runtimeTargets"))
                            foreach (var asset in group.Value.EnumerateObject())
                                if (asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                    && (group.Name == "runtime" || (asset.Value.TryGetProperty("assetType", out var kind) && kind.GetString() == "runtime")))
                                    _privateNames.Add(Path.GetFileNameWithoutExtension(asset.Name));
        }
    }

    internal Assembly LoadEntry(string path)
    {
        EnsureOwned(path);
        // Streams avoid holding the entry file open, but do not imply hot reload/unload support.
        using var image = File.OpenRead(path);
        return LoadFromStream(image);
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == "LesTool.Platform") return typeof(ILesPlugin).Assembly;
        if (name.Name == "Serilog") return typeof(Log).Assembly;
        if (IsSharedWpf(name.Name)) return Default.LoadFromAssemblyName(name);

        var resolved = _resolver.ResolveAssemblyToPath(name);
        if (resolved is null)
        {
            var local = Path.Combine(_directory, name.CultureName ?? "", name.Name + ".dll");
            if (File.Exists(local)) resolved = local;
        }
        if (resolved is not null)
        {
            EnsureOwned(resolved);
            var actual = AssemblyName.GetAssemblyName(resolved);
            if (!AssemblyName.ReferenceMatchesDefinition(name, actual) || (name.Version is not null && actual.Version != name.Version))
                throw new FileLoadException("插件依赖版本不匹配：" + name.FullName, resolved);
            using var image = File.OpenRead(resolved);
            return LoadFromStream(image);
        }
        if (_privateNames.Contains(name.Name ?? "") && name.Name?.EndsWith(".resources", StringComparison.OrdinalIgnoreCase) != true)
            throw new FileNotFoundException("插件清单声明的私有依赖缺失：" + name.FullName);
        if (IsFramework(name.Name)) return Default.LoadFromAssemblyName(name);
        // ResourceManager must be able to fall back from a missing satellite to neutral resources.
        if (name.Name?.EndsWith(".resources", StringComparison.OrdinalIgnoreCase) == true) return null;
        // Never borrow a missing private dependency from another plugin or the host's cache.
        throw new FileNotFoundException("插件私有依赖缺失：" + name.FullName, name.Name + ".dll");
    }

    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var resolved = _resolver.ResolveUnmanagedDllToPath(name);
        if (resolved is null)
        {
            var local = Path.Combine(_directory, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll");
            if (File.Exists(local)) resolved = local;
        }
        if (resolved is null) return IntPtr.Zero; // OS libraries (user32/kernel32/etc.).
        EnsureOwned(resolved);
        Log.Debug("插件原生依赖：{Context} {Library} → {Path}", Name, name, resolved);
        return LoadUnmanagedDllFromPath(resolved);
    }

    private void EnsureOwned(string path)
    {
        if (!PathBoundary.Contains(_directory, Path.GetFullPath(path)))
            throw new FileLoadException("插件依赖越过所属目录", path);
    }

    private static bool IsSharedWpf(string? name) => name is "WindowsBase" or "PresentationCore" or "PresentationFramework"
        or "ReachFramework" or "System.Printing" or "WindowsFormsIntegration" or "Accessibility" or "PresentationUI"
        or "UIAutomationClient" or "UIAutomationClientSideProviders" or "UIAutomationProvider" or "UIAutomationTypes"
        || name?.StartsWith("PresentationFramework.", StringComparison.Ordinal) == true;

    private static bool IsFramework(string? name) => name is "mscorlib" or "netstandard" or "System"
        or "Microsoft.CSharp" or "Microsoft.VisualBasic" or "Microsoft.VisualBasic.Core"
        or "Microsoft.Win32.Primitives" or "Microsoft.Win32.Registry" or "Microsoft.Win32.SystemEvents"
        or "WindowsFormsIntegration" or "Accessibility" or "PresentationUI"
        or "WindowsBase" or "PresentationCore" or "PresentationFramework" or "ReachFramework"
        or "UIAutomationClient" or "UIAutomationClientSideProviders" or "UIAutomationProvider" or "UIAutomationTypes"
        || name?.StartsWith("System.", StringComparison.Ordinal) == true
        || name?.StartsWith("PresentationFramework.", StringComparison.Ordinal) == true;
}
