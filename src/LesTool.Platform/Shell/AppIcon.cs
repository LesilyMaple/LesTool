using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace LesTool.Platform;

/// <summary>
/// 进程图标提取缓存（平台服务）：exe 路径 → SHGetFileInfo 大图标 → 冻结的 BitmapSource。
/// 提取失败（无权限/文件消失）返回 null，由调用方退回落渲染。
/// 宿主托盘图标与音量插件的应用行共用。
/// </summary>
public static class AppIcon
{
    private static readonly Dictionary<string, ImageSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        if (Cache.TryGetValue(exePath, out var cached)) return cached;

        ImageSource? source = null;
        try
        {
            var info = new ShfileInfo();
            var result = SHGetFileInfoW(exePath, 0, ref info,
                (uint)Marshal.SizeOf<ShfileInfo>(), ShgfiIcon | ShgfiLargeIcon);
            if (result != IntPtr.Zero && info.HIcon != IntPtr.Zero)
            {
                source = Imaging.CreateBitmapSourceFromHIcon(
                    info.HIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                DestroyIcon(info.HIcon);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "图标提取失败：{Path}", exePath);
        }

        Cache[exePath] = source; // 失败也缓存，避免每个轮询周期重复碰壁
        return source;
    }

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShfileInfo
    {
        public IntPtr HIcon;
        public int IIcon;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attributes,
        ref ShfileInfo info, uint size, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
