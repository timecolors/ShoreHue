using System;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>插件运行时守卫：安全模式 + 异常熔断（借鉴 Windhawk 的"缩小爆炸半径 + 恢复通道"）。</summary>
public class PluginRuntimeGuardTests
{
    public PluginRuntimeGuardTests() => PluginRuntimeGuard.ResetAllForTests();

    [Fact]
    public void 连续失败达阈值_触发熔断并可从名单里看到()
    {
        Assert.False(PluginRuntimeGuard.IsDisabled("p1"));
        Assert.False(PluginRuntimeGuard.ReportFailure("p1", "测试", "e1"));
        Assert.False(PluginRuntimeGuard.ReportFailure("p1", "测试", "e2"));
        Assert.True(PluginRuntimeGuard.ReportFailure("p1", "测试", "e3"));
        Assert.True(PluginRuntimeGuard.IsDisabled("p1"));
        Assert.Contains("p1", PluginRuntimeGuard.TrippedKeys);
    }

    [Fact]
    public void 未达阈值_不熔断()
    {
        PluginRuntimeGuard.ReportFailure("p2", "测试", "e1");
        PluginRuntimeGuard.ReportFailure("p2", "测试", "e2");
        Assert.False(PluginRuntimeGuard.IsDisabled("p2"));
        Assert.DoesNotContain("p2", PluginRuntimeGuard.TrippedKeys);
    }

    [Fact]
    public void 已熔断_不重复计数()
    {
        for (int i = 0; i < 3; i++) PluginRuntimeGuard.ReportFailure("p4", "测试", "e");
        Assert.False(PluginRuntimeGuard.ReportFailure("p4", "测试", "e"));
    }

    [Fact]
    public void 解除后恢复()
    {
        for (int i = 0; i < 3; i++) PluginRuntimeGuard.ReportFailure("p3", "测试", "e");
        Assert.True(PluginRuntimeGuard.IsDisabled("p3"));
        PluginRuntimeGuard.Clear("p3");
        Assert.False(PluginRuntimeGuard.IsDisabled("p3"));
        Assert.DoesNotContain("p3", PluginRuntimeGuard.TrippedKeys);
    }

    [Fact]
    public void 安全模式_一律停用且不再熔断()
    {
        PluginRuntimeGuard.EnterSafeMode("测试");
        Assert.True(PluginRuntimeGuard.SafeMode);
        Assert.True(PluginRuntimeGuard.IsDisabled("anything"));
        Assert.False(PluginRuntimeGuard.ReportFailure("x", "测试", "e"));
        PluginRuntimeGuard.ResetAllForTests();
        Assert.False(PluginRuntimeGuard.SafeMode);
    }

    [Fact]
    public void 异常识别_按程序集定位插件()
    {
        PluginRuntimeGuard.RegisterAssembly(typeof(PluginRuntimeGuardTests).Assembly, "test-plugin");
        var ex = Record.Exception(() => ThrowFromHere())!;
        Assert.Equal("test-plugin", PluginRuntimeGuard.IdentifyPlugin(ex));
        Assert.Equal("test-plugin", PluginRuntimeGuard.ReportException(ex, "测试"));
        Assert.Null(PluginRuntimeGuard.IdentifyPlugin(new InvalidOperationException("来自非插件")));
    }

    private static void ThrowFromHere() => throw new InvalidOperationException("boom");
}