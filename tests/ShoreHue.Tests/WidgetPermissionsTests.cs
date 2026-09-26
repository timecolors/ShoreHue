using ShoreHue.UI.Widgets.Dynamic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>验证源码权限检测：联网/剪贴板/文件/进程/系统/窗口/屏幕/硬件开关 自动标注。</summary>
public class WidgetPermissionsTests
{
    /// <summary>
    /// ★ 权限文案现在走 resx（以前是硬编码中文 → 英文界面下安装弹窗显示中文），
    ///   于是断言必须**显式固定语言**，否则会随测试进程的 CurrentUICulture 时绿时红。
    /// </summary>
    private static void WithCulture(string culture, System.Action body)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            body();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Detect_HttpClient_FlagsNetwork()
    {
        var perms = WidgetPermissions.Detect("using System.Net.Http; var c = new HttpClient();");
        Assert.Contains("network", perms);
        Assert.DoesNotContain("file", perms);
    }

    /// <summary>硬件开关（亮度/蓝牙/Wi-Fi/热点）：宿主 API 在沙箱面是开放的，必须至少在安装时告知。</summary>
    [Fact]
    public void Detect_DeviceToggles_FlagsDevice()
    {
        Assert.Contains("device", WidgetPermissions.Detect("DisplayBrightness.Set(50);"));
        Assert.Contains("device", WidgetPermissions.Detect("await SystemRadios.SetStateAsync(RadioKind.WiFi, true);"));
        Assert.Contains("device", WidgetPermissions.Detect("await HotspotControl.SetAsync(true);"));
        // 不相关的源码不该被误标
        Assert.DoesNotContain("device", WidgetPermissions.Detect("var x = 1 + 1;"));
    }

    [Fact]
    public void Detect_Clipboard_FlagsClipboard()
    {
        var perms = WidgetPermissions.Detect("System.Windows.Clipboard.SetText(" + "\"" + "hi" + "\"" + ");");
        Assert.Contains("clipboard", perms);
    }

    [Fact]
    public void Detect_FileIo_FlagsFile()
    {
        var perms = WidgetPermissions.Detect("File.WriteAllText(" + "\"" + "a.txt" + "\"" + ", " + "\"" + "x" + "\"" + "); using System.IO;");
        Assert.Contains("file", perms);
    }

    [Fact]
    public void Detect_ProcessStart_FlagsProcess()
    {
        var perms = WidgetPermissions.Detect("Process.Start(" + "\"" + "notepad.exe" + "\"" + ");");
        Assert.Contains("process", perms);
    }

    [Fact]
    public void Detect_WindowApi_FlagsWindow()
    {
        var perms = WidgetPermissions.Detect("FindWindow(null, " + "\"" + "title" + "\"" + "); SendMessage(hwnd, 0x10, 0, 0);");
        Assert.Contains("window", perms);
    }

    [Fact]
    public void Detect_ScreenCapture_FlagsScreen()
    {
        var perms = WidgetPermissions.Detect("Graphics.CopyFromScreen(0, 0, 0, 0, size);");
        Assert.Contains("screen", perms);
    }

    [Fact]
    public void Detect_HarmlessUi_ReturnsEmpty()
    {
        var perms = WidgetPermissions.Detect("var tb = new TextBlock { Text = " + "\"" + "hello" + "\"" + " };");
        Assert.Empty(perms);
    }

    [Fact]
    public void Describe_Empty_ShowsNoPermission()
    {
        WithCulture("zh-CN", () =>
        {
            Assert.Equal("无权限", WidgetPermissions.Describe(new System.Collections.Generic.List<string>()));
            Assert.Equal("无权限", WidgetPermissions.Describe(null));
        });
    }

    [Fact]
    public void Describe_Multiple_JoinsLabels()
    {
        WithCulture("zh-CN", () =>
        {
            var perms = WidgetPermissions.Describe(new[] { "network", "clipboard" });
            // 注：这两条曾是 `Assert.Contains("", perms)` —— 对任意字符串恒真，等于没有断言。
            // 现在直接比对拼装结果（Describe 用空格连接各标签，顺序 = 传入顺序）。
            Assert.Equal("联网 剪贴板", perms);
        });
    }

    /// <summary>
    /// 英文界面下权限文案必须是英文。
    /// ★ 这条测试的意义：以前这套 label/后果文案是**硬编码中文**，英文用户在看安装风险提示时
    ///   读到的却是中文 —— 而"安装时如实告知风险"正是这套提示存在的唯一理由。
    /// </summary>
    [Fact]
    public void 英文界面下_权限文案为英文()
    {
        WithCulture("en-US", () =>
        {
            Assert.Equal("Network", WidgetPermissions.PermissionLabel("network"));
            Assert.Equal("Device toggles", WidgetPermissions.PermissionLabel("device"));
            string detail = WidgetPermissions.DescribeConsequences(new[] { "device", "file" });
            Assert.Contains("brightness", detail);
            Assert.DoesNotContain("屏幕", detail);
        });
    }

    /// <summary>硬件开关必须出现在安装告知的句子里（不能只是内部代号）。</summary>
    [Fact]
    public void 硬件开关_有可读的后果说明()
    {
        WithCulture("zh-CN", () =>
        {
            string detail = WidgetPermissions.DescribeConsequences(new[] { "device" });
            Assert.Contains("硬件开关", detail);
            Assert.Contains("亮度", detail);
        });
    }
}
