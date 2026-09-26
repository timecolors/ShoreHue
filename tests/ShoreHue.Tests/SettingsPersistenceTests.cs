using System;
using System.IO;
using System.Text.Json;
using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 配置**落盘层**的回归护栏（`SettingsFileManager` + `SettingsManager` 的写盘语义）。
///
/// 之前这一层完全没有测试：`SettingsDataJsonTests` 只往返裸 `JsonSerializer`，
/// `SettingsFileManager` 在整个测试树里只出现在注释中。于是下列真实风险全部无人看守：
///   · 原子写（临时文件 + File.Replace）留下的残渣；
///   · config.json 损坏时的备份与降级；
///   · 旧版"呼出/隐藏共用"动画设置的一次性迁移；
///   · ★「先由别人写文件、再 Reload」被内存旧快照覆盖（整套预设 / 一键恢复 / 云端恢复全走这条）。
/// </summary>
[Collection("WidgetStore")]   // 会改 AppPaths.TestDataRoot，必须串行
public class SettingsPersistenceTests : IDisposable
{
    private readonly string _dir;

    public SettingsPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sh_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AppPaths.TestDataRoot = _dir;
    }

    public void Dispose()
    {
        AppPaths.TestDataRoot = null;
        try { Directory.Delete(_dir, true); } catch { /* 尽力而为：临时目录残留无害 */ }
    }

    private string Cfg => Path.Combine(_dir, "config.json");

    private static double OpacityOf(string path)
    {
        var d = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(path));
        return d?.Opacity ?? double.NaN;
    }

    // ==================== ★ 核心回归：外部写盘不能被内存旧快照盖回 ====================

    [Fact]
    public void 别人刚写进配置文件的改动_不会被随后的Reload覆盖()
    {
        var mgr = new SettingsManager();                 // 构造时锁定 _configPath

        // 1) 设置窗口保存（走 Apply）
        mgr.Apply(new SettingsData { Opacity = 0.50 });
        Assert.Equal(0.50, OpacityOf(Cfg), 3);

        // 2) 另一个组件直接写文件（PresetManager.ApplyPreset / BtnRestore_Click / 云端恢复）
        SettingsFileManager.Save(new SettingsData { Opacity = 0.90 }, Cfg);
        Assert.Equal(0.90, OpacityOf(Cfg), 3);

        // 3) 调用方按既有约定 Reload 让设置生效
        mgr.Reload();

        Assert.Equal(0.90, OpacityOf(Cfg), 3);           // 文件没被回滚
        Assert.Equal(0.90, mgr.Opacity, 3);              // 内存也跟上了新文件
    }

    [Fact]
    public void 连续两次Apply_后Reload_读到的是最后一次的值()
    {
        var mgr = new SettingsManager();
        mgr.Apply(new SettingsData { Opacity = 0.10 });
        mgr.Apply(new SettingsData { Opacity = 0.20 });
        mgr.Reload();
        Assert.Equal(0.20, OpacityOf(Cfg), 3);
        Assert.Equal(0.20, mgr.Opacity, 3);
    }

    // ==================== SettingsFileManager 本体 ====================

    [Fact]
    public void 保存是原子的_不留下临时文件()
    {
        SettingsFileManager.Save(new SettingsData { Opacity = 0.33 }, Cfg);

        Assert.True(File.Exists(Cfg));
        Assert.False(File.Exists(Cfg + ".tmp"));
        Assert.Equal(0.33, OpacityOf(Cfg), 3);
    }

    [Fact]
    public void 保存成功返回true_目标目录不可写时返回false()
    {
        Assert.True(SettingsFileManager.Save(new SettingsData(), Cfg));

        // 指向一个不存在的目录 → 写入必然失败，必须如实返回 false（而不是静默吞掉）
        string bad = Path.Combine(_dir, "no-such-dir", "config.json");
        Assert.False(SettingsFileManager.Save(new SettingsData(), bad));
    }

    [Fact]
    public void 文件不存在时返回默认配置_且不落盘()
    {
        var data = SettingsFileManager.Load(Cfg);
        Assert.NotNull(data);
        Assert.False(File.Exists(Cfg));           // Load 不该顺手创建文件
    }

    [Fact]
    public void 文件损坏时_备份原文件并降级为默认配置()
    {
        File.WriteAllText(Cfg, "{ this is not json ");
        string original = File.ReadAllText(Cfg);

        var data = SettingsFileManager.Load(Cfg);

        Assert.NotNull(data);                                   // 降级而不是抛异常
        Assert.True(File.Exists(Cfg + ".bak"));                 // 原文件被保住
        Assert.Equal(original, File.ReadAllText(Cfg + ".bak")); // 且是**原始**内容，不是空配置
        Assert.Equal(original, File.ReadAllText(Cfg));          // 坏文件本身没有被覆写
    }

    [Fact]
    public void 旧版共用动画设置_一次性迁移到触发隐藏分设()
    {
        // 旧版只有 ShowHideEasingType / ShowHideDurationMs
        File.WriteAllText(Cfg, """
        {
          "ShowHideEasingType": "ElasticEase",
          "ShowHideDurationMs": 250,
          "ShowAnimationType": "",
          "ShowAnimationDurationMs": 0,
          "HideAnimationType": "",
          "HideAnimationDurationMs": 0
        }
        """);

        var data = SettingsFileManager.Load(Cfg);

        Assert.Equal("Elastic", data.ShowAnimationType);
        Assert.Equal(250, data.ShowAnimationDurationMs);
        Assert.Equal("Elastic", data.HideAnimationType);
        Assert.Equal(250, data.HideAnimationDurationMs);

        // 迁移结果应已落盘（否则每次启动都要重迁）
        var reread = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(Cfg))!;
        Assert.Equal("Elastic", reread.ShowAnimationType);
    }

    [Fact]
    public void 已经分设过的动画设置_不会被迁移碰到()
    {
        File.WriteAllText(Cfg, """
        {
          "ShowHideEasingType": "ElasticEase",
          "ShowHideDurationMs": 250,
          "ShowAnimationType": "Zoom",
          "ShowAnimationDurationMs": 400,
          "HideAnimationType": "Fade",
          "HideAnimationDurationMs": 120
        }
        """);

        var data = SettingsFileManager.Load(Cfg);

        Assert.Equal("Zoom", data.ShowAnimationType);
        Assert.Equal(400, data.ShowAnimationDurationMs);
        Assert.Equal("Fade", data.HideAnimationType);
        Assert.Equal(120, data.HideAnimationDurationMs);
    }
}
