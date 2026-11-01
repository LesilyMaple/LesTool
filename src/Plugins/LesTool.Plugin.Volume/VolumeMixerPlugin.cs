using LesTool.Platform;
using Serilog;

namespace LesTool.Plugin.Volume;

/// <summary>
/// 音量混音器插件入口：Core Audio 数据层 + EarTrumpet 式浮窗整体随本插件装载，
/// 提供 volume.mixer 命令（热键/命令面板/轮盘槽位均可触发）。
/// </summary>
public sealed class VolumeMixerPlugin : ILesPlugin
{
    private AudioMixerService? _audio;
    private VolumeMixerWindow? _window;

    public string Id => "volume";

    public void Initialize(ILesHost host)
    {
        _audio = new AudioMixerService();
        host.OwnCleanup(() => _audio?.Dispose());
        _audio.Initialize(); // 枚举器创建失败不抛：无声环境插件仍可装载

        _window = new VolumeMixerWindow(_audio);
        host.OwnCleanup(() => _window?.Close());
        _window.Hidden += () => Log.Information("音量混音器已收起");
        _window.PrepareForFastShow(); // 预创建 HWND，触发只做定位 + Show

        host.RegisterCommand("volume.mixer", "音量混音器", "\uE767", _ => Toggle(),
            keywords: "音量 混音 volume mixer");
    }

    private void Toggle() => _window?.Toggle();

    public void Shutdown()
    {
        _window?.Close();
    }
}
