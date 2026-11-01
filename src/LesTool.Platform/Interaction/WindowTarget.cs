using System.Runtime.InteropServices;
namespace LesTool.Platform;
public readonly record struct WindowTarget(nint Handle, uint ProcessId, uint ThreadId)
{
    public static WindowTarget Capture() => FromHandle(GetForegroundWindow());
    public static WindowTarget FromHandle(nint handle)
    {
        var thread = GetWindowThreadProcessId(handle, out var process);
        return new(handle, process, thread);
    }
    public bool IsAlive => Handle != 0 && IsWindow(Handle) && FromHandle(Handle) == this;
    public bool IsForeground => IsAlive && GetForegroundWindow() == Handle;
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
}
