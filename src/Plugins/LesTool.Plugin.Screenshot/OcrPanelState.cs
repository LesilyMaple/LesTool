namespace LesTool.Plugin.Screenshot;

/// <summary>
/// OCR 面板状态（会话内共享，UI 线程）：识别结果逐行存放，
/// 遮罩窗据此在选区右侧渲染面板、在选区画面上画绿框。
/// 选区一旦变动（进入任何拖拽态）就应 Clear，避免框与画面错位。
/// </summary>
internal sealed class OcrPanelState
{
    private readonly List<ScreenshotOcr.OcrLine> _lines = new();

    public IReadOnlyList<ScreenshotOcr.OcrLine> Lines => _lines;
    public bool HasResult { get; private set; }

    public event Action? Changed;

    public void Set(IEnumerable<ScreenshotOcr.OcrLine> lines)
    {
        _lines.Clear();
        _lines.AddRange(lines);
        HasResult = true;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (!HasResult)
            return;
        _lines.Clear();
        HasResult = false;
        Changed?.Invoke();
    }
}
