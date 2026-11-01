using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using LesTool.Platform;
using Serilog;
namespace LesTool.Services;

internal static class ContextProvider
{
    internal static async Task<LesContext> BuildAsync(ContextKind kind, TriggerEnvelope trigger, CancellationToken token)
    {
        string? text = null, clipboard = null, title = null;
        double? age = null; nint focus = 0;
        IReadOnlyList<string>? files = null;
        var scenario = trigger.Source switch { TriggerSource.Hotkey => CommandScenarios.Hotkey,
            TriggerSource.Radial => CommandScenarios.Radial, _ => CommandScenarios.Palette };
        if (kind.HasFlag(ContextKind.Focus) && trigger.Target.IsAlive)
        {
            focus = trigger.Target.Handle;
            try { var buffer = new StringBuilder(256); if (GetWindowText(focus, buffer, buffer.Capacity) > 0) title = buffer.ToString(); }
            catch (Exception e) { Log.Debug(e, "焦点上下文不可用"); }
        }
        if (kind.HasFlag(ContextKind.Clipboard))
            try { if (Clipboard.ContainsText()) { clipboard = Clipboard.GetText(); age = ClipboardService.AgeMsOfLastUpdate(); } }
            catch (Exception e) { Log.Debug(e, "剪贴板上下文不可用"); }
        if (kind.HasFlag(ContextKind.SelectedText) && ScenarioCapabilities.Provides(scenario, ContextKind.SelectedText))
            try { text = await SelectionText.AcquireAsync(trigger.Target, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Log.Debug(e, "文本上下文不可用"); }
        if (kind.HasFlag(ContextKind.SelectedFiles) && ScenarioCapabilities.Provides(scenario, ContextKind.SelectedFiles))
            try { files = (await ExplorerSelection.GetSelectionAsync(trigger.Target, token))?.Paths; }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { Log.Debug(e, "文件上下文不可用"); }
        return new(text, clipboard, age, kind.HasFlag(ContextKind.Cursor) ? trigger.Cursor : default,
            title, focus, files, trigger.Source);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
}
