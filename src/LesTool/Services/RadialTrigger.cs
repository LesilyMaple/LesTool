using System.Windows;

namespace LesTool.Services;

/// <summary>
/// 轮盘触发状态机：右键按住期间，移动距离超过 5px（屏幕物理像素）即触发显示；
/// 松开右键触发隐藏。只被钩子线程调用，纯计算、无 IO、无锁。
/// </summary>
public sealed class RadialTrigger
{
    private const double ThresholdPx = 5.0;

    /// <summary>按住拖动超过阈值。参数为触发时光标的屏幕物理像素坐标。</summary>
    public event Action<Point>? Triggered;

    /// <summary>按住并显示过后松开右键。</summary>
    public event Action? Released;

    private bool _tracking;
    private bool _shown;
    private Point _origin;

    /// <summary>当前手势是否已越过阈值弹出轮盘（拦截决策用：松开时回放单击还是整段吞掉）。</summary>
    public bool IsShown => _shown;

    public void OnRightButtonDown(Point point)
    {
        _tracking = true;
        _shown = false;
        _origin = point;
    }

    public void OnMouseMove(Point point)
    {
        if (!_tracking || _shown) return;

        var dx = point.X - _origin.X;
        var dy = point.Y - _origin.Y;
        if (dx * dx + dy * dy < ThresholdPx * ThresholdPx) return;

        _shown = true;
        Triggered?.Invoke(point);
    }

    public void OnRightButtonUp(Point point)
    {
        if (!_tracking) return;
        _tracking = false;

        if (_shown)
        {
            _shown = false;
            Released?.Invoke();
        }
    }
}
