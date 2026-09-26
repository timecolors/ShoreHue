using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.UI.Widgets;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 窄接口边界回归（2026-09 安全复查）。
///
/// 背景（真实漏洞，已修）：`HostCapabilities.Settings` 返回 `ISettingsService`，而那个接口当时带着
/// `SetPluginTrusted` —— 外来插件可以给自己算好内容哈希、登记信任，**下次加载直接跳过沙箱**（持久化提权）。
/// 同批还修了：`RecordLaunch` 自我授权（插件先把 cmd.exe 记进最近使用清单，再让 `OpenExternally` 放行）、
/// `ms-*` 协议整族放行。
///
/// 本文件的守卫是**结构性**的：谁要是把信任写加回对外接口，或把 addIfMissing 的默认值改掉，这里立刻红。
/// </summary>
[Collection("WidgetStore")]   // ★ 会改 AppPaths.TestDataRoot，必须串行（见 WidgetStoreCollection 注释）
public class HostCapabilityBoundaryTests : IDisposable
{
    private readonly string _dir;

    public HostCapabilityBoundaryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sh_cap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AppPaths.TestDataRoot = _dir;
    }

    public void Dispose()
    {
        AppPaths.TestDataRoot = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ============ ① 信任的读写在宿主侧，插件拿到的接口里没有 ============

    [Fact]
    public void 对外设置接口_不得含信任读写()
    {
        foreach (var name in new[] { "IsPluginTrusted", "SetPluginTrusted", "RevokePluginTrust" })
        {
            Assert.Null(typeof(ISettingsService).GetMethod(name));
        }
    }

    [Fact]
    public void 设置管理器_公开面上也不得含信任读写()
    {
        // 显式接口实现 → 这些成员不在 SettingsManager 的公开皮上，
        // 插件即便拿到实例（或 new 出来）也调不到；只有宿主能经 internal 接口调用。
        foreach (var name in new[] { "IsPluginTrusted", "SetPluginTrusted", "RevokePluginTrust" })
        {
            Assert.Null(typeof(SettingsManager).GetMethod(name, BindingFlags.Public | BindingFlags.Instance));
        }
    }

    [Fact]
    public void 信任库接口_必须是internal()
    {
        var t = typeof(SettingsManager).Assembly.GetType("ShoreHue.Core.Services.Configuration.IPluginTrustStore");
        Assert.NotNull(t);
        Assert.False(t!.IsPublic, "IPluginTrustStore 一旦变成 public，插件就能引用它并调用信任写");
    }

    [Fact]
    public void 对外设置接口_不得含整表替换与落盘入口()
    {
        // ★ 背景：只把 SetPluginTrusted 移出接口是不够的 —— `Apply(SettingsData)` 仍然能整表替换配置，
        //   而 `SettingsData.TrustedPlugins` 是公开可写属性，于是
        //   `HostCapabilities.Settings.Apply(new SettingsData { TrustedPlugins = … })`
        //   就是一条完整的自授权通道：插件给自己（乃至任意包）登记信任并落盘，
        //   下次加载 `ComputeTrust` 第一步即 trustedByHash → 整个沙箱被跳过。
        // 这三/四个成员一旦回到对外接口上，本测试立刻红。
        foreach (var name in new[] { "Apply", "Reload", "SaveSettings", "SetCustomPanels" })
        {
            Assert.Null(typeof(ISettingsService).GetMethod(name));
        }
        // SetCustomPanels 只存在于宿主面
        Assert.NotNull(typeof(ISettingsHost).GetMethod("SetCustomPanels"));
    }

    [Fact]
    public void 对外设置接口的自定义面板列表_必须是只读()
    {
        var prop = typeof(ISettingsService).GetProperty("CustomPanels");
        Assert.NotNull(prop);
        Assert.True(prop!.CanRead);
        Assert.False(prop.CanWrite, "CustomPanels 一旦可写，插件就能整表替换面板列表（含 TrustedSource 标志）");
    }

    [Fact]
    public void 宿主面接口_必须是internal()
    {
        var t = typeof(SettingsManager).Assembly.GetType("ShoreHue.Core.Services.Configuration.ISettingsHost");
        Assert.NotNull(t);
        Assert.False(t!.IsPublic, "ISettingsHost 一旦变成 public，插件就能整表改写 config.json（信任表就在里面）");
    }

    [Fact]
    public void 设置管理器_公开面上不得暴露可写的自定义面板列表()
    {
        var prop = typeof(SettingsManager).GetProperty("CustomPanels");
        Assert.NotNull(prop);
        Assert.False(prop!.SetMethod?.IsPublic ?? false,
            "SettingsManager.CustomPanels 的 setter 必须是 internal：公开面不得存在整表替换入口");
    }

    // ============ ①B 宿主"写文件"封装也必须在沙箱黑名单里 ============

    [Theory]
    [InlineData("ShoreHue.Core.Services.SettingsFileManager")]
    [InlineData("ShoreHue.Core.Services.Configuration.PresetManager")]
    [InlineData("ShoreHue.Core.Services.Configuration.SettingsManager")]
    public void 宿主写文件封装_必须被沙箱拦截(string typeName)
    {
        // SettingsFileManager.Save(data, path) 是 public static 且收**任意路径** ——
        // 不拦就等于一条绕过 File.Write* 白名单的任意写文件通道（可覆盖 exe / config.json）。
        Assert.True(WidgetCompiler.IsHostTypeBlocked(typeName),
            $"{typeName} 必须在宿主特权黑名单里，否则外来代码可经它写任意文件");
    }

    // ============ ② 最近使用清单是"宿主产生的白名单"，插件只能刷新不能新增 ============

    [Fact]
    public void 插件路径的RecordLaunch_不得新增条目()
    {
        string evil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        // 插件那条路：不新增。
        // ★ 这里**故意不**再调用宿主版 RecentAppTracker.RecordLaunch 做对照 ——
        //   那条路会往真实的 HKCU UserAssist 写记录（开发机/CI runner 的最近使用列表被污染），
        //   且断言依赖这个进程外全局状态。安全断言（插件路径不新增）本身已经完整。
        HostCapabilities.RecordLaunch(evil);
        Assert.DoesNotContain(RecentAppTracker.GetRecentApps(50), a => string.Equals(a.Path, evil, StringComparison.OrdinalIgnoreCase));
    }

    // ============ ③ ms-* 只放行 ms-settings:，其余协议族一律拒绝 ============

    [Theory]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("ms-officecmd:")]
    [InlineData("search-ms:query=x")]
    public void 非设置类ms协议_必须拒绝(string uri)
    {
        Assert.False(HostCapabilities.OpenExternally(uri));
    }
}
