namespace AuthIsland.Animation;

/// <summary>
/// 一维弹簧。每一帧积分一次，驱动窗口尺寸 / 圆角的连续形变。
/// 相比 Storyboard 的关键差异：目标可以在动画中途被改写，速度是连续的，
/// 所以连续切换状态时不会出现「重新起跑」的顿挫感 —— 这正是灵动岛的手感来源。
/// </summary>
public sealed class SpringValue
{
    private double _value;
    private double _target;
    private double _velocity;

    private readonly double _stiffness;
    private readonly double _damping;

    public SpringValue(double initial, double stiffness = 320, double damping = 26)
    {
        _value = initial;
        _target = initial;
        _stiffness = stiffness;
        _damping = damping;
    }

    public double Value => _value;
    public double Target => _target;

    public void SetTarget(double target) => _target = target;

    /// <summary>立刻跳到目标值，用于初始化 / 关闭动画。</summary>
    public void Snap(double value)
    {
        _value = value;
        _target = value;
        _velocity = 0;
    }

    /// <summary>推进一帧；返回 true 表示仍在运动。</summary>
    public bool Tick(double dt)
    {
        // 半隐式欧拉：先更新速度再更新位置，比显式欧拉稳定得多
        double force = -_stiffness * (_value - _target) - _damping * _velocity;
        _velocity += force * dt;
        _value += _velocity * dt;

        if (Math.Abs(_value - _target) < 0.08 && Math.Abs(_velocity) < 0.6)
        {
            _value = _target;
            _velocity = 0;
            return false;
        }

        return true;
    }
}
