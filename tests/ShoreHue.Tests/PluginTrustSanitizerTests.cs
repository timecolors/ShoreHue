using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 信任表的外部输入净化（`PluginTrustSanitizer`）。
///
/// 背景（真实漏洞）：信任表的唯一凭据是 `config.json` 里的 `TrustedPlugins[id] = 内容哈希`，
/// 而 `ComputeTrust` **第一步**就判 `trustedByHash` → 命中即整个沙箱被跳过。
/// 于是任何一条"外部数据 → SettingsData → 落盘"的通路只要不清理它，
/// 一条记录就能让受害机上的外来包永久免检，而 `docs/SECURITY.md` 明确承诺
/// 「预设文件不能替代码授信」。三条通路：预设文件 / 云端配置 / .shpkg 包。
/// </summary>
[Collection("WidgetStore")]   // 会改 AppPaths.TestDataRoot / PresetManager.TestPresetsDir，必须串行
public class PluginTrustSanitizerTests : IDisposable
{
    private readonly string _dir;

    public PluginTrustSanitizerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sh_trustsan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AppPaths.TestDataRoot = _dir;
        PresetManager.TestPresetsDir = Path.Combine(_dir, "Presets");
    }

    public void Dispose()
    {
        AppPaths.TestDataRoot = null;
        PresetManager.TestPresetsDir = null;
        try { Directory.Delete(_dir, true); } catch { /* 尽力而为：临时目录残留无害 */ }
    }

    private string Cfg => Path.Combine(_dir, "config.json");

    private static SettingsData WithTrust(params (string id, string hash)[] entries)
    {
        var d = new SettingsData { TrustedPlugins = new Dictionary<string, string>() };
        foreach (var (id, hash) in entries) d.TrustedPlugins[id] = hash;
        return d;
    }

    // ==================== 纯函数语义 ====================

    [Fact]
    public void 本机没有任何信任记录时_外部信任全部丢弃()
    {
        var data = WithTrust(("a", "h1"), ("b", "h2"));
        int dropped = PluginTrustSanitizer.StripExternalTrust(data, null, "测试预设");

        Assert.Equal(2, dropped);
        Assert.Null(data.TrustedPlugins);
    }

    [Fact]
    public void 同id同哈希_保留()
    {
        var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["a"] = "h1" };
        var data = WithTrust(("a", "h1"));

        Assert.Equal(0, PluginTrustSanitizer.StripExternalTrust(data, local, "测试预设"));
        Assert.NotNull(data.TrustedPlugins);
        Assert.Equal("h1", data.TrustedPlugins!["a"]);
    }

    [Fact]
    public void 同id但哈希不同_丢弃_内容一变信任即失效()
    {
        var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["a"] = "h1" };
        var data = WithTrust(("a", "TAMPERED"));

        Assert.Equal(1, PluginTrustSanitizer.StripExternalTrust(data, local, "测试预设"));
        Assert.Null(data.TrustedPlugins);
    }

    [Fact]
    public void 本机没有的新id_丢弃()
    {
        var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["known"] = "h1" };
        var data = WithTrust(("known", "h1"), ("injected", "hX"));

        Assert.Equal(1, PluginTrustSanitizer.StripExternalTrust(data, local, "测试预设"));
        Assert.NotNull(data.TrustedPlugins);
        Assert.True(data.TrustedPlugins!.ContainsKey("known"));
        Assert.False(data.TrustedPlugins.ContainsKey("injected"));
    }

    [Fact]
    public void 没有信任表字段时_返回0且不报错()
    {
        Assert.Equal(0, PluginTrustSanitizer.StripExternalTrust(new SettingsData(), null, "测试预设"));
    }

    // ==================== 端到端：预设注入打不进来 ====================

    [Fact]
    public void 预设文件里的信任记录_不会进入本机配置()
    {
        // 本机已有一份信任记录
        var local = WithTrust(("mine", "hMine"));
        local.Opacity = 0.10;
        SettingsFileManager.Save(local, Cfg);

        // 恶意预设：既改设置，又塞进一条"外来包免检"的信任记录
        var evil = WithTrust(("mine", "hMine"), ("victim_pkg", "hVictim"));
        evil.Opacity = 0.42;
        PresetManager.SaveFull("evil", evil);

        var mgr = new SettingsManager();
        Assert.True(PresetManager.ApplyPreset("evil", mgr), "预设本身应当应用成功（只是信任记录被丢掉）");

        var applied = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(Cfg))!;
        Assert.Equal(0.42, applied.Opacity, 3);                                   // 设置照常生效
        Assert.NotNull(applied.TrustedPlugins);
        Assert.True(applied.TrustedPlugins!.ContainsKey("mine"));                 // 本机原有信任保留
        Assert.False(applied.TrustedPlugins.ContainsKey("victim_pkg"),           // 注入的被丢弃
            "预设文件不能替代码授信：注入的信任记录必须被净化掉");
    }

    [Fact]
    public void 预设名里的路径分隔符_不会造成两个预设互相覆盖()
    {
        // 以前 Sanitize 是"删掉非法字符"，于是 a/b 与 ab 撞到同一个文件名，后者静默覆盖前者
        PresetManager.SaveFull("a/b", new SettingsData { Opacity = 0.11 });
        PresetManager.SaveFull("ab", new SettingsData { Opacity = 0.22 });

        var names = PresetManager.ListPresets();
        Assert.Equal(2, names.Count);

        var p1 = PresetManager.LoadPreset("a/b");
        var p2 = PresetManager.LoadPreset("ab");
        Assert.NotNull(p1);
        Assert.NotNull(p2);
        Assert.Equal(0.11, p1!.Opacity, 3);
        Assert.Equal(0.22, p2!.Opacity, 3);
    }
}
