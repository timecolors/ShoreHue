using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using System;
using System.IO;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>逐区域动画覆盖：解析优先区域、缺省跟随全局；清除恢复继承（隔离临时目录，不碰真实配置）。</summary>
[Collection("WidgetStore")]   // ★ 会改 AppPaths.TestDataRoot，必须串行（见 WidgetStoreCollection 注释）
public class RegionAnimationTests : IDisposable
{
    private readonly string _dir;

    public RegionAnimationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sh_anim_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AppPaths.TestDataRoot = _dir;
    }

    public void Dispose()
    {
        AppPaths.TestDataRoot = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void NoOverride_FallsBackToGlobal()
    {
        var mgr = new SettingsManager();
        mgr.Apply(new SettingsData { ShowAnimationType = "Zoom", ShowAnimationDurationMs = 400 });
        Assert.Equal("Zoom", mgr.GetResolvedShowAnimationType("Top_Left"));
        Assert.Equal(400, mgr.GetResolvedShowAnimationDurationMs("Top_Left"));
        // 全局隐藏动画类型为空 → 按兼容逻辑继承全局触发类型（Zoom）
        Assert.Equal("Zoom", mgr.GetResolvedHideAnimationType("Top_Left"));
    }

    [Fact]
    public void Override_TakesPrecedence()
    {
        var mgr = new SettingsManager();
        mgr.Apply(new SettingsData { ShowAnimationType = "Zoom", ShowAnimationDurationMs = 400 });
        mgr.SetRegionAnimation("Top_Left", new ShoreHue.Core.Models.RegionAnimationOverride
        {
            ShowAnimationType = "Fade",
            ShowAnimationDurationMs = 150
        });
        Assert.Equal("Fade", mgr.GetResolvedShowAnimationType("Top_Left"));
        Assert.Equal(150, mgr.GetResolvedShowAnimationDurationMs("Top_Left"));
        // 未覆盖的隐藏动画仍跟随全局（全局隐藏空 → 继承触发类型 Zoom）
        Assert.Equal("Zoom", mgr.GetResolvedHideAnimationType("Top_Left"));
        Assert.Equal("Zoom", mgr.GetResolvedShowAnimationType("Bottom_Center")); // 其他区域不受影响
    }

    [Fact]
    public void ClearOverride_RestoresGlobal()
    {
        var mgr = new SettingsManager();
        mgr.Apply(new SettingsData { ShowAnimationType = "Zoom", ShowAnimationDurationMs = 400 });
        mgr.SetRegionAnimation("Top_Left", new ShoreHue.Core.Models.RegionAnimationOverride { ShowAnimationType = "Fade" });
        Assert.Equal("Fade", mgr.GetResolvedShowAnimationType("Top_Left"));
        mgr.SetRegionAnimation("Top_Left", null);
        Assert.Equal("Zoom", mgr.GetResolvedShowAnimationType("Top_Left"));
        Assert.Null(mgr.GetRegionAnimation("Top_Left"));
    }

    [Fact]
    public void EmptyOverride_IsTreatedAsNone()
    {
        var mgr = new SettingsManager();
        mgr.SetRegionAnimation("Top_Left", new ShoreHue.Core.Models.RegionAnimationOverride());
        Assert.Null(mgr.GetRegionAnimation("Top_Left"));
    }
}
