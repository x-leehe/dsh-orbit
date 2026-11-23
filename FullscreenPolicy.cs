namespace DshOrbit;

/// <summary>
/// 全屏自动隐藏的状态机（纯逻辑，不碰窗口 / 定时器 / 配置以外的东西）。
///
/// 两条状态：<see cref="Hidden"/>（这次隐藏是不是全屏导致的，恢复时只认它）与
/// <see cref="Suppressed"/>（全屏期间用户手动唤出过，本次全屏内不再自动藏回去）。
/// <see cref="Update"/> 只返回「该做什么」，真正的 Hide()/Show() 由 WidgetApp 执行。
/// </summary>
internal sealed class FullscreenPolicy
{
    public enum Step
    {
        None,
        Hide,
        Show,
    }

    /// <summary>当前这次隐藏来自全屏自动隐藏（不是用户动作）。</summary>
    public bool Hidden { get; private set; }

    /// <summary>全屏期间用户手动唤出过：退出全屏前不再自动隐藏。</summary>
    public bool Suppressed { get; private set; }

    /// <summary>
    /// 心跳推进。参数是当前的「开关 / 探测结果 / 用户是否想看到挂件」，返回该执行的动作。
    /// 语义与旧实现一致：开关关闭或退出全屏时恢复；用户本来就不想看到挂件时不恢复。
    /// </summary>
    public Step Update(bool enabled, bool isFullscreen, bool widgetVisible)
    {
        if (!enabled || !isFullscreen)
        {
            Suppressed = false;
            if (!Hidden)
            {
                return Step.None;
            }

            Hidden = false;
            return widgetVisible ? Step.Show : Step.None;
        }

        if (Hidden || Suppressed || !widgetVisible)
        {
            return Step.None;
        }

        Hidden = true;
        return Step.Hide;
    }

    /// <summary>
    /// 用户在全屏期间手动唤出（托盘左键 / 显示挂件）：
    /// 清掉本次全屏的隐藏，并压制到退出全屏为止。
    /// </summary>
    public void Wake(bool isFullscreen)
    {
        Hidden = false;
        if (isFullscreen)
        {
            Suppressed = true;
        }
    }

    /// <summary>用户明确隐藏挂件：不再需要「退出全屏后恢复」。</summary>
    public void UserHid() => Hidden = false;

    /// <summary>「全屏时隐藏」开关被切换：清掉压制，让新设置立刻生效。</summary>
    public void ResetSuppression() => Suppressed = false;
}
