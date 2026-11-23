using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// FullscreenPolicy：全屏自动隐藏的状态机。
/// 关键语义：只隐藏「全屏 + 开关开 + 用户想看到」的挂件；退出全屏只恢复自己藏起来的；
/// 全屏期间用户手动唤出过，就整段压制到退出全屏。
/// </summary>
public class FullscreenPolicyTests
{
    [Fact]
    public void StartsIdle()
    {
        var policy = new FullscreenPolicy();

        Assert.False(policy.Hidden);
        Assert.False(policy.Suppressed);
        Assert.Equal(FullscreenPolicy.Step.None, policy.Update(enabled: true, isFullscreen: false, widgetVisible: true));
    }

    [Fact]
    public void Fullscreen_HidesOnce_ThenRestoresOnExit()
    {
        var policy = new FullscreenPolicy();

        Assert.Equal(FullscreenPolicy.Step.Hide, policy.Update(true, true, true));
        Assert.True(policy.Hidden);

        // 仍然全屏：不重复隐藏
        Assert.Equal(FullscreenPolicy.Step.None, policy.Update(true, true, true));

        // 退出全屏：恢复
        Assert.Equal(FullscreenPolicy.Step.Show, policy.Update(true, false, true));
        Assert.False(policy.Hidden);
        Assert.Equal(FullscreenPolicy.Step.None, policy.Update(true, false, true));
    }

    [Fact]
    public void DisabledSwitch_NeverHides_AndRestoresIfHidden()
    {
        var policy = new FullscreenPolicy();

        Assert.Equal(FullscreenPolicy.Step.None, policy.Update(false, true, true));
        Assert.False(policy.Hidden);

        Assert.Equal(FullscreenPolicy.Step.Hide, policy.Update(true, true, true));
        Assert.Equal(FullscreenPolicy.Step.Show, policy.Update(false, true, true));
        Assert.False(policy.Hidden);
    }

    [Fact]
    public void WakeDuringFullscreen_SuppressesUntilExit()
    {
        var policy = new FullscreenPolicy();
        Assert.Equal(FullscreenPolicy.Step.Hide, policy.Update(true, true, true));

        // 用户从托盘唤出：隐藏清掉 + 压制
        policy.Wake(isFullscreen: true);
        Assert.False(policy.Hidden);
        Assert.True(policy.Suppressed);

        // 全屏仍在：不再自动隐藏
        Assert.Equal(FullscreenPolicy.Step.None, policy.Update(true, true, true));

        // 退出全屏：压制解除，没藏过所以不用恢复
        Assert.Equal(FullscreenPolicy.Step.None, policy.Update(true, false, true));
        Assert.False(policy.Suppressed);

        // 下一次全屏重新生效
        Assert.Equal(FullscreenPolicy.Step.Hide, policy.Update(true, true, true));
    }

    [Fact]
    public void WakeWithoutFullscreen_DoesNotSuppress()
    {
        var policy = new FullscreenPolicy();
        policy.Wake(isFullscreen: false);

        Assert.False(policy.Hidden);
        Assert.False(policy.Suppressed);
        Assert.Equal(FullscreenPolicy.Step.Hide, policy.Update(true, true, true));
    }

    [Fact]
    public void UserHidesWhileAutoHidden_ExitDoesNotRestore()
    {
        var policy = new FullscreenPolicy();
        Assert.Equal(FullscreenPolicy.Step.Hide, policy.Update(true, true, true));

        // 托盘点「隐藏挂件」
        policy.UserHid();
        Assert.Equal(FullscreenPolicy.Step.None, policy.Update(true, false, widgetVisible: false));
        Assert.False(policy.Hidden);
    }

    [Fact]
    public void WidgetNotVisible_NeverHides()
    {
        var policy = new FullscreenPolicy();

        Assert.Equal(FullscreenPolicy.Step.None, policy.Update(true, true, widgetVisible: false));
        Assert.False(policy.Hidden);
    }

    [Fact]
    public void ResetSuppression_AllowsHideAgain()
    {
        var policy = new FullscreenPolicy();
        policy.Wake(isFullscreen: true);
        Assert.True(policy.Suppressed);

        policy.ResetSuppression();
        Assert.False(policy.Suppressed);
        Assert.Equal(FullscreenPolicy.Step.Hide, policy.Update(true, true, true));
    }
}
