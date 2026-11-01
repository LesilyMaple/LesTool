using System.IO;
using LesTool.Platform;

namespace LesTool.Services.Plugins;

/// <summary>插件资产访问实现：根目录锚定 plugins/&lt;id&gt;/assets/，防路径越界。</summary>
internal sealed class PluginAssets : IPluginAssets
{
    private readonly string _root;

    public PluginAssets(string root) => _root = Path.GetFullPath(root);

    public string? Resolve(string relativePath)
    {
        string full = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!PathBoundary.Contains(_root, full))
            return null; // 越界（../ 逃出资产目录）
        return File.Exists(full) ? full : null;
    }

    public IReadOnlyList<string> List(string relativeDir, string pattern = "*")
    {
        string dir = Path.GetFullPath(Path.Combine(_root, relativeDir));
        if (!PathBoundary.Contains(_root, dir) || !Directory.Exists(dir))
            return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
            .Select(p => p[_root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList();
    }
}
