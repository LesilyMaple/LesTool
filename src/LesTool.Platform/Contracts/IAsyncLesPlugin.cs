namespace LesTool.Platform;
/// <summary>Optional cooperative business stop. Return only after owned in-flight work has ended.</summary>
public interface IAsyncLesPlugin : ILesPlugin
{
    Task ShutdownAsync(CancellationToken cancellationToken);
}
