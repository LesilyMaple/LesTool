using System.IO;
using Serilog;

namespace LesTool.Plugin.DownloadRouter;

/// <summary>
/// 安全搬移：目标夹不存在自动建；重名自动加 " (n)" 后缀永不覆盖；
/// 搬前再独占开一次确认（巡检查过与真正动手之间的空窗兜底）；
/// 被占用 IOException 重试 3 次间隔 2 秒，穷尽抛出由调用方记失败账。
/// 任何路径不删源文件（File.Move 同卷即改名；跨卷由运行时复制+删源，中断自动回滚不留半份）。
/// </summary>
internal static class FileMover
{
    private const int RetryMax = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>返回最终落点（重名时为加后缀后的路径）；失败抛 IOException/UnauthorizedAccessException。</summary>
    public static string Move(string source, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        for (int attempt = 1; ; attempt++)
        {
            // 目标名每次重算：极小概率的"查完重名到落盘前被占位"竞争，重试时换下一个可用名
            string dest = ConflictFreePath(targetDir, Path.GetFileName(source));
            try
            {
                using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                File.Move(source, dest);
                return dest;
            }
            catch (IOException) when (attempt < RetryMax)
            {
                Log.Debug("搬移受阻，第 {N}/{Max} 次重试：{File}", attempt, RetryMax, source);
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private static string ConflictFreePath(string dir, string fileName)
    {
        string candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate)) return candidate;
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int n = 2; n < 1000; n++)
        {
            candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException($"重名冲突无法生成可用文件名：{fileName}");
    }
}
