using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.Volume;

/// <summary>
/// 音量混音器插件入口：Core Audio 数据层 + EarTrumpet 式浮窗整体随本插件装载。
/// 托盘图标属平台（应用身份），本插件只提供 volume.mixer 命令；插件缺席时
/// 托盘左键经回退链转命令面板（宿主 VolumeMixerController）。
/// </summary>
public sealed class VolumeMixerPlugin : ILesPlugin
{
    private AudioMixerService? _audio;
    private VolumeMixerWindow? _window;

    public string Id => "volume";

    public void Initialize(ILesHost host)
    {
        _audio = new AudioMixerService();
        _audio.Initialize(); // 枚举器创建失败不抛：无声环境插件仍可装载

        _window = new VolumeMixerWindow(_audio);
        _window.Hidden += () => Log.Information("音量混音器已收起");
        _window.PrepareForFastShow(); // 预创建 HWND，托盘触发只做定位 + Show

        host.RegisterCommand("volume.mixer", "音量混音器", "\uE767", _ => Toggle(),
            keywords: "音量 混音 volume mixer");
    }

    private void Toggle() => _window?.ToggleFromTray();

    public void Shutdown()
    {
        _window?.HideAnimated(); // 停轮询
        _window?.Close();
    }
}
