using System;
using System.IO;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.UI.Widgets.Dynamic;
using ShoreHue.Infrastructure.Utils;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 安全 v2 信任模型：信任以「插件 id + 内容哈希」为凭据（不是包内自述）。
/// 覆盖：哈希匹配才受信 / 内容一变即失效 / 撤销 / 市场来源不自动受信。
/// </summary>
[Collection("WidgetStore")]   // ★ 会改 AppPaths.TestDataRoot，必须串行（见 WidgetStoreCollection 注释）
public class PluginTrustTests : IDisposable
{
    private readonly string _dir;

    public PluginTrustTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sh_trust_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AppPaths.TestDataRoot = _dir;
    }

    public void Dispose()
    {
        AppPaths.TestDataRoot = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void 未记录_不受信()
    {
        IPluginTrustStore s = new SettingsManager();
        Assert.False(s.IsPluginTrusted("widget_a", "hash1"));
    }

    [Fact]
    public void 记录后受信_内容变化即失效()
    {
        IPluginTrustStore s = new SettingsManager();
        s.SetPluginTrusted("widget_a", "hash1");
        Assert.True(s.IsPluginTrusted("widget_a", "hash1"));

        // ★ 内容变了（哈希不同）→ 旧信任不适用
        Assert.False(s.IsPluginTrusted("widget_a", "hash2"));
    }

    [Fact]
    public void 撤销后不受信()
    {
        IPluginTrustStore s = new SettingsManager();
        s.SetPluginTrusted("widget_a", "hash1");
        s.RevokePluginTrust("widget_a");
        Assert.False(s.IsPluginTrusted("widget_a", "hash1"));
    }

    [Fact]
    public void 哈希只随内容变化_与书写顺序无关()
    {
        string a = ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.ContentHash("class A {}", "", "");
        string b = ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.ContentHash("class A {}", "", "");
        string c = ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.ContentHash("class A { }", "", "");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }


    // ===== 信任判定（纯函数，安全 v2 修正版）=====

    [Fact]
    public void 无标记的文件夹代码_受信_海床初衷()
        => Assert.True(WidgetPluginStore.ComputeTrust(hostOriginMarker: false, manifestTrusted: null,
                                                      explicitlySandboxed: false, trustedByHash: false));

    [Fact]
    public void 宿主标记的外来包_不可信()
        => Assert.False(WidgetPluginStore.ComputeTrust(true, null, false, false));

    [Fact]
    public void 宿主写入的_trustedSource_false_不可信()
        => Assert.False(WidgetPluginStore.ComputeTrust(false, false, false, false));

    [Fact]
    public void 用户点过改用沙箱_即使有标记也受信不了()
        => Assert.False(WidgetPluginStore.ComputeTrust(true, true, explicitlySandboxed: true, trustedByHash: true));

    [Fact]
    public void 用户点过信任_外来包也受信()
        => Assert.True(WidgetPluginStore.ComputeTrust(true, false, false, trustedByHash: true));

    [Fact]
    public void 包内自述_true_不能覆盖宿主标记()
        => Assert.False(WidgetPluginStore.ComputeTrust(true, true, false, false));

    [Fact]
    public void 改用沙箱_写入哨兵并生效()
    {
        IPluginTrustStore s = new SettingsManager();
        s.SetPluginTrusted("widget_x", WidgetPluginStore.DenySentinel);
        Assert.True(s.IsPluginTrusted("widget_x", WidgetPluginStore.DenySentinel));
        // 哨兵不等于内容哈希 → 不会把任何真实内容判成"受信"
        Assert.False(s.IsPluginTrusted("widget_x", "somehash"));
    }

    [Theory]
    [InlineData("using System.Diagnostics; class A { void M(){ System.Diagnostics.Process.Start(\"calc\"); } }")]
    [InlineData("class A { void M(){ new System.IO.StreamWriter(\"a\").Write(1); } }")]
    [InlineData("class A { void M(){ ShoreHue.Infrastructure.WinApi.UninstallHelper.LaunchUninstall(true); } }")]
    public void 不可信内容的典型形态_确实会被沙箱拦截(string src)
        => Assert.NotEqual("", ShoreHue.UI.Widgets.Dynamic.WidgetCompiler.SandboxErrors(src));
}
