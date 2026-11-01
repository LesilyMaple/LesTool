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

    /// <summary>轮盘显示期间的每次鼠标移动（屏幕物理像素）。扇区悬停高亮的数据源。</summary>
    public event Action<Point>? Moved;

    /// <summary>按住并显示过后松开右键。</summary>
    public event Action? Released;

    /// <summary>越过阈值后、触发显示前的同步闸（钩子线程调用，须快速、无锁、无 IO）。
    /// 返回 true = 当前软件禁用轮盘：本次手势不触发，松开时按普通右键单击回放。</summary>
    public Func<bool>? SuppressTrigger { get; set; }

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
        if (!_tracking) return;

        if (_shown)
        {
            // 显示期持续上报位置（扇区悬停高亮）；同线程顺序派发，松开前最后一次移动必先于松开到达
            Moved?.Invoke(point);
            return;
        }

        var dx = point.X - _origin.X;
        var dy = point.Y - _origin.Y;
        if (dx * dx + dy * dy < ThresholdPx * ThresholdPx) return;

        if (SuppressTrigger?.Invoke() == true)
        {
            // 本软件禁用轮盘：手势就此作罢（视为普通右键，松开时钩子按单击回放）
            _tracking = false;
            return;
        }

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
