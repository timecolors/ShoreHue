using ShoreHue.Tests;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>安全模式判定（纯函数）：命令行 / 用户请求 / 连续未正常退出。</summary>
public class SafeModeDecisionTests
{
    [Fact] public void 命令行指定_安全模式()
    {
        Assert.True(ShoreHue.App.DecideSafeMode(true, false, false, 0, out int n));
        Assert.Equal(0, n);
    }

    [Fact] public void 用户请求_安全模式且计数清零()
        => Assert.True(ShoreHue.App.DecideSafeMode(false, true, false, 2, out int n));

    [Fact] public void 连续三次未正常退出_自动安全模式()
    {
        // 已有 2 次 + 本次标记还在 = 3 → 安全模式
        Assert.True(ShoreHue.App.DecideSafeMode(false, false, true, 2, out int n));
        Assert.Equal(0, n);
    }

    [Fact] public void 安全模式判定_计数清零但原因串写真实次数()
    {
        var (safe, newCount, reason) = ShoreHue.App.EvaluateSafeMode(false, false, true, 2);
        Assert.True(safe);
        Assert.Equal(0, newCount);                    // 落盘的新计数被清零（安全模式启动后重新计数）
        Assert.Equal("连续 3 次未正常退出", reason);   // ★ 原因串必须写 3，不是 0
    }

    [Fact] public void 三种安全模式原因_各自说清楚_未达阈值时无原因串()
    {
        Assert.Equal("命令行 --safe-mode", ShoreHue.App.EvaluateSafeMode(true, false, false, 0).Reason);
        Assert.Equal("用户请求（托盘：以安全模式重启）", ShoreHue.App.EvaluateSafeMode(false, true, false, 0).Reason);
        Assert.Equal("", ShoreHue.App.EvaluateSafeMode(false, false, false, 0).Reason);

        var (safe, n, reason) = ShoreHue.App.EvaluateSafeMode(false, false, true, 1);
        Assert.False(safe);
        Assert.Equal(2, n);
        Assert.Equal("", reason);
    }

    [Fact] public void 未达三次_普通启动且计数累加()
    {
        Assert.False(ShoreHue.App.DecideSafeMode(false, false, true, 1, out int n));
        Assert.Equal(2, n);
        Assert.False(ShoreHue.App.DecideSafeMode(false, false, false, 0, out int n2));
        Assert.Equal(0, n2);
    }
}