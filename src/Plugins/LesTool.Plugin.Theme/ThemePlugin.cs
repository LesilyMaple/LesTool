using System.Runtime.InteropServices;
using LesTool.Platform;
using Microsoft.Win32;
using Serilog;

namespace LesTool.Plugin.Theme;

/// <summary>
/// 系统深浅色插件入口：提供 theme.toggle 命令（热键/命令面板/轮盘槽位均可触发）。
/// 翻转 HKCU\...\Themes\Personalize 的两个 DWORD 后广播 WM_SETTINGCHANGE，
/// 让资源管理器立即响应——不广播的话任务栏要注销重登才变。
/// 宿主 ThemeManager 在跟随系统模式下监听系统深浅，切完应用皮肤会自动跟着换。
/// </summary>
public sealed class ThemePlugin : ILesPlugin
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private const int WmSettingChange = 0x001A;
    private const int SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam,
        int flags, int timeout, out IntPtr result);

    public string Id => "theme";

    public void Initialize(ILesHost host)
    {
        host.RegisterCommand("theme.toggle", "切换系统深浅色", "\uE793", _ => Toggle(host),
            keywords: "深色 浅色 dark light 主题");
        Log.Debug("theme 插件已注册命令：theme.toggle");
    }

    private static void Toggle(ILesHost host)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key is null)
            {
                Log.Error("注册表项不存在，无法切换系统深浅色：{Path}", KeyPath);
                host.Toast("系统深浅色：注册表项不存在");
                return;
            }

            var wasLight = Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 1;
            var now = wasLight ? 0 : 1;
            key.SetValue("AppsUseLightTheme", now, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", now, RegistryValueKind.DWord);

            // 广播设置变更；超时 1 秒，卡死的接收方直接跳过
            SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, "ImmersiveColorSet",
                SmtoAbortIfHung, 1000, out _);

            Log.Information("系统深浅色已切换：{From} → {To}", wasLight ? "浅色" : "深色", wasLight ? "深色" : "浅色");
            host.Toast(wasLight ? "已切换为深色" : "已切换为浅色");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "切换系统深浅色失败");
            host.Toast("系统深浅色切换失败");
        }
    }

    public void Shutdown()
    {
    }
}
