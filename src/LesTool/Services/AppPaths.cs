using System.IO;

namespace LesTool.Services;

internal static class AppPaths
{
    internal static string DataRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LesTool");
}
