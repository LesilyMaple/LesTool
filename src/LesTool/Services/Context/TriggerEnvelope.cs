using System.Windows;
using LesTool.Platform;
namespace LesTool.Services;

internal sealed record TriggerEnvelope(Guid Id, TriggerSource Source, WindowTarget Target, Point Cursor, DateTime CapturedAt)
{
    internal static TriggerEnvelope Capture(TriggerSource source, TriggerSnapshot? snapshot = null)
    {
        var target = snapshot?.Target ?? (snapshot is null ? WindowTarget.Capture() : WindowTarget.FromHandle(snapshot.FocusWindowHandle));
        return new(Guid.NewGuid(), source, target, GdiCapture.GetCursorPosition(), DateTime.UtcNow);
    }
}
