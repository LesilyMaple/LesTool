using System.Text;
using System.Windows;

namespace LesTool.Services;

public sealed record GestureCandidate(string Sequence, Point EndPoint);

/// <summary>
/// 手势识别状态机：右键按下后按 30px 距离阈值采样方向（简化为 R/D/L/U 四向），
/// 每接受一个方向重新锚定原点（stroke 风格）。松开右键时若已有方向则输出手势。
/// 只被钩子线程调用；纯计算、无 IO、无锁，保证钩子回调即时返回。
/// </summary>
public sealed class GestureRecognizer
{
    private const double DirectionThreshold = 30.0;

    public event Action<GestureCandidate>? GestureRecognized;

    private bool _tracking;
    private Point _anchor;
    private readonly StringBuilder _sequence = new(8);

    public void OnRightButtonDown(Point point)
    {
        _tracking = true;
        _anchor = point;
        _sequence.Clear();
    }

    public void OnMouseMove(Point point)
    {
        if (!_tracking) return;

        var dx = point.X - _anchor.X;
        var dy = point.Y - _anchor.Y;
        if (dx * dx + dy * dy < DirectionThreshold * DirectionThreshold) return;

        var direction = Math.Abs(dx) > Math.Abs(dy)
            ? (dx > 0 ? 'R' : 'L')
            : (dy > 0 ? 'D' : 'U');
        if (_sequence.Length == 0 || _sequence[^1] != direction)
            _sequence.Append(direction);
        _anchor = point;
    }

    public void OnRightButtonUp(Point point)
    {
        if (_tracking && _sequence.Length > 0)
            GestureRecognized?.Invoke(new GestureCandidate(_sequence.ToString(), point));

        _tracking = false;
        _sequence.Clear();
    }
}
