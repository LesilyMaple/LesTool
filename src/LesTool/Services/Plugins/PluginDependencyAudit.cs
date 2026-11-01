using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
namespace LesTool.Services.Plugins;

/// <summary>Managed graphs are private; same-name native libraries still require one package version.</summary>
internal static class PluginDependencyAudit
{
    // Only paths the current Windows x64 loader may use. Old nested build outputs and other RIDs are not dependencies.
    internal static IEnumerable<string> RuntimeLibraries(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.dll")) yield return file;
        foreach (var rid in new[] { "win-x64", "win" })
        {
            var native = Path.Combine(directory, "runtimes", rid, "native");
            if (Directory.Exists(native))
                foreach (var file in Directory.EnumerateFiles(native, "*.dll", SearchOption.AllDirectories)) yield return file;
        }
    }

    internal static bool IsManaged(string file)
    {
        try { using var image = File.OpenRead(file); using var pe = new PEReader(image); return pe.HasMetadata; }
        catch (BadImageFormatException) { return false; }
    }

    internal static Dictionary<string, string> Check(IEnumerable<string> directories)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var known = new Dictionary<string, (string Owner, string Hash)>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
            try
            {
                foreach (var file in RuntimeLibraries(directory))
                {
                    if (IsManaged(file))
                    {
                        var identity = System.Reflection.AssemblyName.GetAssemblyName(file);
                        if (identity.Name is "LesTool.Platform" or "Serilog")
                            errors[directory] = "插件重复携带共享契约/日志库：" + Path.GetFileName(file);
                        continue;
                    }
                    var key = Path.GetFileName(file);
                    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
                    if (known.TryGetValue(key, out var previous) && previous.Hash != hash)
                    {
                        var reason = "插件原生依赖冲突：" + key + "（" + Path.GetFileName(previous.Owner) + " / " + Path.GetFileName(directory) + "）";
                        errors[directory] = reason; errors[previous.Owner] = reason;
                    }
                    else known[key] = (directory, hash);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException) { errors[directory] = error.Message; }
        return errors;
    }
}
