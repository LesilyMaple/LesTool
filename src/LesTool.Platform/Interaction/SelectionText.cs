using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using Serilog;

namespace LesTool.Platform;

public static class SelectionText
{
    private static readonly SemaphoreSlim ClipboardGate = new(1, 1);
    public static bool IsClipboardOperationActive => ClipboardGate.CurrentCount == 0;

    // Compatibility queries; production triggers use the asynchronous APIs below.
    public static string? Acquire() => QueryTextAsync(WindowTarget.Capture()).GetAwaiter().GetResult().Value;
    public static bool ProbeSelection() => !string.IsNullOrWhiteSpace(Acquire());

    public static async Task<bool> ProbeSelectionAsync(WindowTarget target, CancellationToken token = default) =>
        !string.IsNullOrWhiteSpace((await QueryTextAsync(target, token)).Value);

    private static Task<QueryResult<string?>> QueryTextAsync(WindowTarget target, CancellationToken token = default) =>
        UiaQueries.Executor.RunAsync(() =>
        {
            if (!target.IsAlive || target.ProcessId == (uint)Environment.ProcessId) return null;
            var element = AutomationElement.FocusedElement;
            if (element is null || element.Current.ProcessId != target.ProcessId || !target.IsForeground) return null;
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
            return ((TextPattern)pattern).GetSelection().Select(range => range.GetText(-1)?.Trim())
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        }, TimeSpan.FromMilliseconds(150), token);

    public static async Task<string?> AcquireAsync(WindowTarget target, CancellationToken token = default)
    {
        if (!target.IsForeground || target.ProcessId == (uint)Environment.ProcessId) return null;
        var query = await QueryTextAsync(target, token);
        if (query.State == QueryState.Cancelled) token.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(query.Value)) return query.Value;
        if (!target.IsForeground || IsConsole(target.Handle)) return null;
        if (!await ClipboardGate.WaitAsync(TimeSpan.FromMilliseconds(300), token)) return null;
        try
        {
            if (!target.IsForeground) return null;
            var baseline = GetClipboardSequenceNumber();
            var (ok, restore) = SnapshotClipboard();
            if (!ok || restore is null || GetClipboardSequenceNumber() != baseline || !target.IsForeground) return null;
            using var copyMark = ClipboardService.BeginSelfWrite(target.ProcessId);
            if (GetClipboardSequenceNumber() != baseline) return null;
            KeyboardEvents.SendChord(true, false, false, 0x43);
            uint copiedSequence = 0;
            try
            {
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 300)
                {
                    await Task.Delay(15, token);
                    if (!target.IsForeground) return null;
                    var sequence = GetClipboardSequenceNumber();
                    if (sequence == baseline) continue;
                    GetWindowThreadProcessId(GetClipboardOwner(), out var owner);
                    if (owner != target.ProcessId) return null;
                    if (copyMark.ObservedSequence is { } observed && observed != sequence) return null;
                    copiedSequence = sequence; copyMark.Observe(sequence);
                    try
                    {
                        var text = Clipboard.ContainsText() ? Clipboard.GetText().TrimEnd('\0') : null;
                        return GetClipboardSequenceNumber() == sequence ? text : null;
                    }
                    catch (Exception error) { Log.Debug(error, "取词剪贴板读取失败"); return null; }
                }
                return null;
            }
            finally
            {
                copiedSequence = copyMark.ObservedSequence ?? copiedSequence;
                if (copiedSequence == 0 && GetClipboardSequenceNumber() != baseline)
                {
                    var candidate = GetClipboardSequenceNumber();
                    GetWindowThreadProcessId(GetClipboardOwner(), out var owner);
                    if (candidate != baseline && owner == target.ProcessId && GetClipboardSequenceNumber() == candidate)
                    { copiedSequence = candidate; copyMark.Observe(candidate); }
                }
                if (copiedSequence != 0 && GetClipboardSequenceNumber() == copiedSequence)
                    await RestoreAsync(restore, copiedSequence);
            }
        }
        finally { ClipboardGate.Release(); }
    }

    private static async Task RestoreAsync(Action restore, uint copiedSequence)
    {
        using var marker = ClipboardService.BeginSelfWrite((uint)Environment.ProcessId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetClipboardSequenceNumber() != copiedSequence) return;
            try { restore(); marker.Observe(GetClipboardSequenceNumber()); return; }
            catch (Exception error) { Log.Debug(error, "取词恢复剪贴板重试"); await Task.Delay(25); }
        }
        ToastService.Show("取词后未能恢复原剪贴板，详见日志", 3000);
    }
    private static readonly HashSet<string> SnapshotFormats = new(StringComparer.Ordinal)
    {
        "UnicodeText", "Text", "System.String", "OEMText", "Locale", "HTML Format", "Rich Text Format",
        "FileDrop", "Preferred DropEffect", "Bitmap", "DeviceIndependentBitmap", "PNG"
    };
    internal static bool CanSnapshotFormats(IEnumerable<string> formats) => formats.All(SnapshotFormats.Contains);
    private static (bool Ok, Action? Restore) SnapshotClipboard()
    {
        try
        {
            var original = Clipboard.GetDataObject();
            var formats = original?.GetFormats(false) ?? Array.Empty<string>();
            if (formats.Length == 0) return (true, Clipboard.Clear);
            // Refuse Office/private or delayed formats instead of restoring a reduced clipboard.
            if (!CanSnapshotFormats(formats)) return (false, null);
            var data = new DataObject();
            foreach (var format in formats)
            {
                var value = original!.GetData(format, false);
                object? copy = value switch
                {
                    string text => text,
                    byte[] bytes => bytes.ToArray(),
                    System.IO.MemoryStream stream => new System.IO.MemoryStream(stream.ToArray()),
                    string[] files => files.ToArray(),
                    System.Collections.Specialized.StringCollection files => CopyFiles(files),
                    System.Windows.Media.Imaging.BitmapSource image => FrozenCopy(image),
                    int number => number,
                    _ => null,
                };
                if (copy is null) return (false, null);
                data.SetData(format, copy, false);
            }
            return (true, () => Clipboard.SetDataObject(data, true));
        }
        catch (Exception error) { Log.Debug(error, "无法完整快照剪贴板，跳过取词兜底"); return (false, null); }
    }
    private static System.Collections.Specialized.StringCollection CopyFiles(System.Collections.Specialized.StringCollection files)
    {
        var result = new System.Collections.Specialized.StringCollection();
        result.AddRange(files.Cast<string>().ToArray()); return result;
    }
    private static System.Windows.Media.Imaging.BitmapSource FrozenCopy(System.Windows.Media.Imaging.BitmapSource image)
    { var clone = image.Clone(); clone.Freeze(); return clone; }
    private static bool IsConsole(nint hwnd)
    {
        var name = new StringBuilder(64); GetClassName(hwnd, name, name.Capacity);
        return name.ToString() is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS";
    }
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder name, int count);
}
