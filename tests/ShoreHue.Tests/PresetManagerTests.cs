using ShoreHue.Core.Services.Configuration;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>验证预设：整套保存/列表/读取、局部预设合并应用、覆盖字段、删除（用临时目录，不碰真实数据）。</summary>
public class PresetManagerTests : IDisposable
{
    private readonly string _dir;

    public PresetManagerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sh_presets_" + Guid.NewGuid().ToString("N"));
        PresetManager.TestPresetsDir = _dir;
    }

    public void Dispose()
    {
        PresetManager.TestPresetsDir = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void SaveFull_List_Load_RoundTrip()
    {
        var data = new SettingsData { Edge_Left = false, ShowAnimationType = "Zoom", TriggerDistancePx = 9 };
        PresetManager.SaveFull("测试整套", data);

        var names = PresetManager.ListPresets();
        Assert.Contains("测试整套", names);

        var loaded = PresetManager.LoadPreset("测试整套");
        Assert.NotNull(loaded);
        Assert.False(loaded!.Edge_Left);
        Assert.Equal("Zoom", loaded.ShowAnimationType);
        Assert.Equal(9, loaded.TriggerDistancePx);
    }

    [Fact]
    public void SavePartial_StoresOnlySubset()
    {
        var data = new SettingsData { Edge_Left = false, ShowAnimationType = "Zoom", TriggerDistancePx = 9 };
        PresetManager.SavePartial("局部", data, new[] { "ShowAnimationType", "TriggerDistancePx" });

        var loaded = PresetManager.LoadPreset("局部");
        Assert.NotNull(loaded);
        Assert.Equal("Zoom", loaded!.ShowAnimationType);
        Assert.Equal(9, loaded.TriggerDistancePx);
    }

    [Fact]
    public void AppliedFields_ReturnsSubsetFields()
    {
        var data = new SettingsData { Edge_Left = false, ShowAnimationType = "Zoom" };
        PresetManager.SavePartial("字段集", data, new[] { "ShowAnimationType" });

        var fields = PresetManager.AppliedFields("字段集");
        Assert.Contains("ShowAnimationType", fields);
        Assert.DoesNotContain("Edge_Left", fields);
    }

    [Fact]
    public void DeletePreset_RemovesFile()
    {
        PresetManager.SaveFull("待删", new SettingsData());
        Assert.True(PresetManager.DeletePreset("待删"));
        Assert.DoesNotContain("待删", PresetManager.ListPresets());
    }

    [Fact]
    public void ListPresets_EmptyDir_ReturnsEmpty()
    {
        Assert.Empty(PresetManager.ListPresets());
    }
}
