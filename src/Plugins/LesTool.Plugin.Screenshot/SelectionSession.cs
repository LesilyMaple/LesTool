using System.Windows;

namespace LesTool.Plugin.Screenshot;

/// <summary>
/// 框选交互状态机。选区矩形统一存虚拟屏物理像素，跨遮罩窗口共享一份。
/// 状态流转：None →(按下)→ DraggingNew →(松手且≥MinSize)→ Selected
///           Selected →(手柄)→ DraggingHandle →(松手)→ Selected
///           Selected →(内部按下)→ DraggingMove →(松手)→ Selected
///           Selected →(外部按下)→ 重新 DraggingNew
/// </summary>
internal sealed class SelectionSession
{
    /// <summary>选区最小尺寸（物理像素）。按下到松开不足此值视为误触，不产生选区。</summary>
    public const double MinSize = 3;

    public enum State { None, DraggingNew, DraggingMove, DraggingHandle, Selected }

    /// <summary>命中区域，由遮罩窗口在鼠标按下前算好传入。</summary>
    public enum Zone { Outside, Inside, Handle }

    private readonly Rect _bounds;   // 虚拟屏边界，选区始终钳制在内
    private Point _anchor;           // 拖拽起点（物理像素）
    private Rect _startRect;         // 移动/调手柄前的原选区
    private int _handle;             // 正在拖的手柄序号

    public Rect Selection { get; private set; }   // 已归一化；None 状态为 Empty
    public State Current { get; private set; } = State.None;

    /// <summary>选区几何或状态变化，遮罩窗口据此重绘。</summary>
    public event Action? Changed;

    /// <summary>回车确认。参数为最终选区（物理像素）。</summary>
    public event Action<Rect>? Confirmed;

    /// <summary>钉图。参数为选区（物理像素），钉在原位。</summary>
    public event Action<Rect>? Pinned;

    /// <summary>另存为（弹保存对话框）。参数为选区（物理像素）。</summary>
    public event Action<Rect>? SaveRequested;

    /// <summary>录屏。参数为选区（物理像素）。</summary>
    public event Action<Rect>? RecordRequested;

    /// <summary>文字识别。参数为选区（物理像素）。</summary>
    public event Action<Rect>? OcrRequested;

    /// <summary>二维码识别。参数为选区（物理像素）。</summary>
    public event Action<Rect>? QrRequested;

    /// <summary>长截图。参数为选区（物理像素）。</summary>
    public event Action<Rect>? LongShotRequested;

    /// <summary>ESC 取消。</summary>
    public event Action? Cancelled;

    public SelectionSession(Rect virtualBounds) => _bounds = virtualBounds;

    public void MouseDown(Point global, Zone zone, int handleIndex)
    {
        switch (Current)
        {
            case State.None:
                BeginNewDrag(global);
                break;

            case State.Selected:
                if (zone == Zone.Handle)
                {
                    _handle = handleIndex;
                    _startRect = Selection;
                    Current = State.DraggingHandle;
                }
                else if (zone == Zone.Inside)
                {
                    _anchor = global;
                    _startRect = Selection;
                    Current = State.DraggingMove;
                }
                else
                {
                    BeginNewDrag(global);
                }
                break;
        }
        Changed?.Invoke();
    }

    public void MouseMove(Point global)
    {
        if (Current is not (State.DraggingNew or State.DraggingMove or State.DraggingHandle))
            return;

        global = Clamp(global);
        switch (Current)
        {
            case State.DraggingNew:
                Selection = new Rect(_anchor, global); // WPF Rect(Point,Point) 自动归一化
                break;

            case State.DraggingMove:
                double x = Math.Clamp(_startRect.X + global.X - _anchor.X,
                    _bounds.Left, _bounds.Right - _startRect.Width);
                double y = Math.Clamp(_startRect.Y + global.Y - _anchor.Y,
                    _bounds.Top, _bounds.Bottom - _startRect.Height);
                Selection = new Rect(x, y, _startRect.Width, _startRect.Height);
                break;

            case State.DraggingHandle:
                Selection = ApplyHandle(_startRect, _handle, global);
                break;
        }
        Changed?.Invoke();
    }

    /// <summary>松开鼠标。draggingNew 结束时若拖拽不足 MinSize 但悬停检测到窗口
    /// （单击），则整窗采纳为选区。</summary>
    public void MouseUp(Point global, Rect? hoveredWindow = null)
    {
        switch (Current)
        {
            case State.DraggingNew:
                bool settled = false;
                if (Selection.Width >= MinSize && Selection.Height >= MinSize)
                {
                    Current = State.Selected;
                    settled = true;
                }
                else if (hoveredWindow is Rect window)
                {
                    // 窗口可能伸出虚拟屏（最小化到屏幕外的窗等），裁到界内
                    Rect clamped = Rect.Intersect(window, _bounds);
                    if (!clamped.IsEmpty && clamped.Width >= MinSize && clamped.Height >= MinSize)
                    {
                        Selection = clamped;
                        Current = State.Selected;
                        settled = true;
                    }
                }
                if (!settled)
                {
                    Selection = Rect.Empty;
                    Current = State.None;
                }
                break;

            case State.DraggingMove:
            case State.DraggingHandle:
                Current = State.Selected;
                break;
        }
        Changed?.Invoke();
    }

    /// <summary>回车确认；仅在已有有效选区时生效。</summary>
    public void Confirm()
    {
        if (Current == State.Selected &&
            Selection.Width >= MinSize && Selection.Height >= MinSize)
        {
            Confirmed?.Invoke(Selection);
        }
    }

    /// <summary>钉图；仅在已有有效选区时生效。</summary>
    public void RequestPin()
    {
        if (Current == State.Selected &&
            Selection.Width >= MinSize && Selection.Height >= MinSize)
        {
            Pinned?.Invoke(Selection);
        }
    }

    /// <summary>另存为；仅在已有有效选区时生效。取消对话框则继续会话。</summary>
    public void RequestSave()
    {
        if (Current == State.Selected &&
            Selection.Width >= MinSize && Selection.Height >= MinSize)
        {
            SaveRequested?.Invoke(Selection);
        }
    }

    /// <summary>对选区录屏；仅在已有有效选区时生效。</summary>
    public void RequestRecord()
    {
        if (Current == State.Selected &&
            Selection.Width >= MinSize && Selection.Height >= MinSize)
        {
            RecordRequested?.Invoke(Selection);
        }
    }

    /// <summary>对选区做文字识别；仅在已有有效选区时生效。</summary>
    public void RequestOcr()
    {
        if (Current == State.Selected &&
            Selection.Width >= MinSize && Selection.Height >= MinSize)
        {
            OcrRequested?.Invoke(Selection);
        }
    }

    /// <summary>对选区识别二维码；仅在已有有效选区时生效。</summary>
    public void RequestQr()
    {
        if (Current == State.Selected &&
            Selection.Width >= MinSize && Selection.Height >= MinSize)
        {
            QrRequested?.Invoke(Selection);
        }
    }

    /// <summary>对选区长截图；仅在已有有效选区时生效。</summary>
    public void RequestLongShot()
    {
        if (Current == State.Selected &&
            Selection.Width >= MinSize && Selection.Height >= MinSize)
        {
            LongShotRequested?.Invoke(Selection);
        }
    }

    /// <summary>ESC 取消，任何状态可用。</summary>
    public void Cancel() => Cancelled?.Invoke();

    /// <summary>
    /// 方向键逐像素微调（物理像素）。moveAll=true 整体平移；
    /// 否则按方向调整对应边缘（右/下方向扩边，左/上方向也是扩边——即 Shift+← 左边左移）。
    /// 仅已选定状态可用。
    /// </summary>
    public void Nudge(double dx, double dy, bool moveAll)
    {
        if (Current != State.Selected || Selection.IsEmpty)
            return;

        if (moveAll)
        {
            double x = Math.Clamp(Selection.X + dx, _bounds.Left, _bounds.Right - Selection.Width);
            double y = Math.Clamp(Selection.Y + dy, _bounds.Top, _bounds.Bottom - Selection.Height);
            Selection = new Rect(x, y, Selection.Width, Selection.Height);
        }
        else
        {
            double l = Selection.Left, t = Selection.Top, r = Selection.Right, b = Selection.Bottom;
            if (dx > 0) r += dx; else if (dx < 0) l += dx;
            if (dy > 0) b += dy; else if (dy < 0) t += dy;
            l = Math.Clamp(l, _bounds.Left, _bounds.Right - MinSize);
            t = Math.Clamp(t, _bounds.Top, _bounds.Bottom - MinSize);
            r = Math.Clamp(r, l + MinSize, _bounds.Right);
            b = Math.Clamp(b, t + MinSize, _bounds.Bottom);
            Selection = new Rect(l, t, r - l, b - t);
        }
        Changed?.Invoke();
    }

    private void BeginNewDrag(Point global)
    {
        _anchor = Clamp(global);
        Selection = new Rect(_anchor, _anchor);
        Current = State.DraggingNew;
    }

    /// <summary>按手柄方向重算矩形，钳制在虚拟屏内且不小于 MinSize。</summary>
    private Rect ApplyHandle(Rect r, int handle, Point p)
    {
        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        switch (handle)
        {
            case 0: left = p.X; top = p.Y; break;    // NW
            case 1: top = p.Y; break;                // N
            case 2: right = p.X; top = p.Y; break;   // NE
            case 3: right = p.X; break;              // E
            case 4: right = p.X; bottom = p.Y; break;// SE
            case 5: bottom = p.Y; break;             // S
            case 6: left = p.X; bottom = p.Y; break; // SW
            case 7: left = p.X; break;               // W
            default: return r;
        }
        left = Math.Clamp(left, _bounds.Left, _bounds.Right - MinSize);
        top = Math.Clamp(top, _bounds.Top, _bounds.Bottom - MinSize);
        right = Math.Clamp(right, left + MinSize, _bounds.Right);
        bottom = Math.Clamp(bottom, top + MinSize, _bounds.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    private Point Clamp(Point p) => new(
        Math.Clamp(p.X, _bounds.Left, _bounds.Right),
        Math.Clamp(p.Y, _bounds.Top, _bounds.Bottom));
}
