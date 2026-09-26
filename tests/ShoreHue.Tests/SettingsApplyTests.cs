using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using System;
using System.IO;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 设置保存链路回归测试（隔离临时目录，不污染真实用户配置）：
/// 曾存在严重 bug——SettingsWindow 只把控件值写入本地 _settingsData 副本，
/// 从不同步进 SettingsManager，导致 _settings.SaveSettings() 保存的是从未更新的旧数据，
/// 设置改动刷新/重启后全部还原（"关掉小鸟依人刷新又开"、面板一直跟随鼠标）。
/// 修复：SettingsManager.Apply(SettingsData) 整体替换内部数据并触发保存。
/// </summary>
[Collection("WidgetStore")]   // ★ 会改 AppPaths.TestDataRoot，必须与其它共享静态状态的测试串行（见 WidgetStoreCollection 注释）
public class SettingsApplyTests : IDisposable
{
    private readonly string _dir;

    public SettingsApplyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sh_apply_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AppPaths.TestDataRoot = _dir;
    }

    public void Dispose()
    {
        AppPaths.TestDataRoot = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Apply_Replaces_Internal_Data_And_Raises_Changed()
    {
        var mgr = new SettingsManager();
        bool changed = false;
        mgr.SettingsChanged += () => changed = true;

        // 模拟设置窗口"用户关掉小鸟依人"：构造一份新副本并 Apply
        var fresh = new SettingsData { ClingModeEnabled = false };
        mgr.Apply(fresh);

        // Apply 后属性读回新值（关键：证明设置窗口改动真正进入 SettingsManager）
        Assert.False(mgr.ClingModeEnabled);
        Assert.True(changed);
    }

    [Fact]
    public void Apply_With_Enabled_Cling_Toggles_Property()
    {
        var mgr = new SettingsManager();
        mgr.Apply(new SettingsData { ClingModeEnabled = true });
        Assert.True(mgr.ClingModeEnabled);
        mgr.Apply(new SettingsData { ClingModeEnabled = false });
        Assert.False(mgr.ClingModeEnabled);
    }
}
