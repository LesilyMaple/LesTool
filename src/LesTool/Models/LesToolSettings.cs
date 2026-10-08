namespace LesTool.Models;

/// <summary>%APPDATA%\LesTool\settings.json 的根模型。新增字段给默认值，保持向后兼容。</summary>
public sealed class LesToolSettings
{
    public int Version { get; set; } = 1;

    /// <summary>多功能面板全局热键，如 "Ctrl+Alt+P"。解析见 HotkeyService.ParseHotkey。</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+P";

    /// <summary>轮盘触发的手势序列（如 "DR"）；空串 = 任意右键拖拽即触发。骨架版未参与判定。</summary>
    public string RadialTriggerGesture { get; set; } = "";

    public bool StartWithWindows { get; set; }
}
