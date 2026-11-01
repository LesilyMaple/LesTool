namespace LesTool.Platform;

/// <summary>Optional writable plugin business data directory. The host controls the profile root.</summary>
public interface IPluginDataDirectory
{
    string DataDirectory { get; }
}
