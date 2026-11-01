using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 截图插件入口：框选/标注/钉图/另存/OCR/长截图/录屏 整体随本插件装载与卸载。
/// 资产三原则落点：OCR 模型与 ffmpeg 均为插件资产（assets/models/v5、assets/ffmpeg.exe），
/// 缺失时核心框选照常可用，OCR/录屏各自降级提示。
/// </summary>
public sealed class ScreenshotPlugin : IAsyncLesPlugin
{
    public string Id => "screenshot";

    public void Initialize(ILesHost host)
    {
        ScreenshotService.Initialize();
        ScreenRecorder.Initialize();
        host.OwnCleanup(ScreenshotService.Shutdown);
        if (host is IPluginLifetimeHost lifetime) lifetime.Lifetime.OnStopping(ScreenshotService.Shutdown);
        host.RegisterCommand("screenshot.region", "截图（框选）", "\uE722", _ =>
        {
            try
            {
                Log.Debug("触发截图");
                ScreenshotService.StartInteractiveCapture();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动截图失败");
                host.Toast($"启动截图失败：{ex.Message}", 4000);
            }
        }, keywords: "截图 框选 screenshot");

        host.OwnCleanup(LongScreenshot.Shutdown);
        host.OwnCleanup(ScreenRecorder.Shutdown);
        // 等价迁移宿主旧模块逻辑：后台预探测编码器（约 1~3 秒），首次录制免 UI 卡顿
        ScreenRecorder.Warmup();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        ScreenshotService.Shutdown();
        LongScreenshot.Shutdown();
        await ScreenRecorder.ShutdownAsync();
    }

    public void Shutdown()
    {
        // 模块表逆序下先于基础服务关停：录制中退出优雅停 ffmpeg 保段、长截图收计时器退订阅
        ScreenRecorder.Shutdown();
        LongScreenshot.Shutdown();
    }
}
