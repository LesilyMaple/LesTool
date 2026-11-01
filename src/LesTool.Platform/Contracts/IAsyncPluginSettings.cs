using LesTool.Platform.Storage;

namespace LesTool.Platform;

public interface IAsyncPluginSettings : IPluginSettings
{
    Task<SaveResult> SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);
    Task<SaveResult> FlushAsync(CancellationToken cancellationToken = default);
}
