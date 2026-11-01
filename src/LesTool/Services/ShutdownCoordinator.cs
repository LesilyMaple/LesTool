using LesTool.Platform;
using LesTool.Services.Plugins;
using Serilog;
namespace LesTool.Services;

internal sealed class ShutdownCoordinator
{
    private Task? _shutdown;
    internal Task StopAsync(Action stopModules) => _shutdown ??= StopCoreAsync(stopModules);
    private static async Task StopCoreAsync(Action stopModules)
    {
        ActionRegistry.Stop();
        HotkeyService.Stop();
        PluginHost.QuiesceAll();
        // Stop producers before flushing. Each module/plugin isolates cleanup failures.
        await ActionRegistry.DrainAsync();
        await PluginHost.ShutdownAllAsync();
        stopModules();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            // PluginHost keeps settings stores separately from active instances until flush.
            await PluginHost.FlushSettingsAsync(deadline.Token);
            var app = await AppSettings.FlushAsync(deadline.Token);
            var state = await PluginState.FlushAsync(deadline.Token);
            if (!app.Succeeded || !state.Succeeded) Log.Error("退出时部分设置未保存：{App} {Plugins}", app.Error, state.Error);
        }
        catch (Exception error) { Log.Error(error, "退出保存未完成"); }
        UiaQueries.Stop();
    }
}
