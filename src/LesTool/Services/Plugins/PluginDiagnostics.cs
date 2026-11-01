using System.IO;
using System.Text.Json;
using LesTool.Platform;
namespace LesTool.Services.Plugins;

internal static class PluginDiagnostics
{
    internal static int Run(string directory, string reportPath)
    {
        var results = new List<object>(); var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = Directory.GetDirectories(directory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var dependencyErrors = PluginDependencyAudit.Check(directories);
        foreach (var plugin in directories)
        {
            try
            {
                if (dependencyErrors.TryGetValue(plugin, out var dependencyError)) throw new InvalidDataException(dependencyError);
                var manifest = JsonSerializer.Deserialize<PluginHost.Manifest>(File.ReadAllText(Path.Combine(plugin, "plugin.json")),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("清单为空");
                if (!ids.Add(manifest.Id) || !string.Equals(Path.GetFileName(plugin), manifest.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("插件 Id 重复或目录不匹配");
                if (manifest.ApiContract != 1 || !Version.TryParse(manifest.MinPlatform, out var required)
                    || required > typeof(ILesPlugin).Assembly.GetName().Version) throw new InvalidDataException("插件契约版本不匹配");
                var entry = Path.GetFullPath(Path.Combine(plugin, manifest.EntryAssembly));
                if (!PathBoundary.Contains(plugin, entry)) throw new InvalidDataException("入口越界");
                var context = new PluginLoadContext(entry);
                using var reflection = context.EnterContextualReflection();
                var assembly = context.LoadEntry(entry);
                var type = assembly.GetType(manifest.EntryType, throwOnError: true)!;
                if (!typeof(ILesPlugin).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                    throw new InvalidDataException("插件入口不满足契约");
                results.Add(new { manifest.Id, manifest.Version, Status = "Loadable", LoadContext = context.Name });
            }
            catch (Exception error) { errors.Add(Path.GetFileName(plugin) + ": " + error.Message); }
        }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Plugins = results, Errors = errors }, new JsonSerializerOptions { WriteIndented = true }));
        return errors.Count == 0 ? 0 : 1;
    }
}
