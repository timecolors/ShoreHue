using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests
{
    /// <summary>
    /// 沙箱"少误伤、别留洞"回归（2026-09-10 安全复查第二轮）。
    ///
    /// 两件事：
    /// ① **数据流（污点）分析的两个已知洞**必须补上 —— 跨方法字段外泄、环境变量外传；
    /// ② 文本层里那批"窗口/输入"关键字规则与符号层**重复**，而它们是裸子串匹配（会命中字符串字面量/注释），
    ///    属于纯误报源。本测试先证明"符号层确实还拦得住"（冗余），再证明"字符串里出现这些词不再被拦"（误报消除）。
    /// </summary>
    public class SandboxPrecisionTests
    {
        // ==================== ① 数据流：两个已知洞 ====================

        [Fact]
        public void 跨方法字段外泄被拦()
        {
            const string src = @"
using System.IO;
using System.Net.Http;
public class A
{
    private string _cache;
    private void Read(string p) { _cache = File.ReadAllText(p); }
    private void Send(string url) { new HttpClient().PostAsync(url, new StringContent(_cache)); }
}";
            Assert.Contains("数据流外泄", WidgetCompiler.SandboxErrors(src));
        }

        [Fact]
        public void 环境变量外传被拦()
        {
            const string src = @"
using System;
using System.Net.Http;
public class A
{
    public void M(string url)
    {
        var key = Environment.GetEnvironmentVariable(""OPENAI_API_KEY"");
        new HttpClient().PostAsync(url, new StringContent(key));
    }
}";
            Assert.Contains("数据流外泄", WidgetCompiler.SandboxErrors(src));
        }

        [Fact]
        public void 宿主服务读取外传被拦()
        {
            const string src = @"
using System.Net.Http;
using ShoreHue.UI.Widgets;
public class A
{
    public void M(string url)
    {
        var notes = HostCapabilities.Notes;
        new HttpClient().PostAsync(url, new StringContent(notes.ToString()));
    }
}";
            Assert.Contains("数据流外泄", WidgetCompiler.SandboxErrors(src));
        }

        [Fact]
        public void 正常联网不被误杀()
        {
            // 查天气：发出去的是固定 URL 和常量，没有任何本地数据流向网络
            const string src = @"
using System.Net.Http;
public class A
{
    public async void M() { await new HttpClient().GetStringAsync(""https://example.com/weather""); }
}";
            Assert.Equal("", WidgetCompiler.SandboxErrors(src));
        }

        [Fact]
        public void 读本地文件不外传不被误杀()
        {
            const string src = @"
using System.IO;
public class A { public string M() => File.ReadAllText(""a.txt""); }";
            Assert.Equal("", WidgetCompiler.SandboxErrors(src));
        }

        // ==================== ② 文本层冗余规则 ====================

        private static string Pinvoke(string fn) =>
            "using System;\nusing System.Runtime.InteropServices;\n" +
            "public class A { [DllImport(\"user32.dll\")] static extern bool " + fn + "(IntPtr h); " +
            "void M() { " + fn + "(IntPtr.Zero); } }";

        [Theory]
        [InlineData("SetForegroundWindow")]
        [InlineData("FindWindow")]
        [InlineData("EnumWindows")]
        [InlineData("SetWindowsHookEx")]
        [InlineData("SendInput")]
        [InlineData("PostMessage")]
        [InlineData("SendMessage")]
        [InlineData("keybd_event")]
        [InlineData("mouse_event")]
        [InlineData("BitBlt")]
        [InlineData("PrintWindow")]
        public void 这些输入注入能力由符号层拦截不靠文本关键字(string fn)
        {
            // 删文本规则的前提：符号层（InteropServices 类型黑名单）确实还拦得住
            Assert.Contains("System.Runtime.InteropServices", WidgetCompiler.SandboxErrors(Pinvoke(fn)));
        }

        [Fact]
        public void 注册表由符号层拦截不靠裸关键字()
        {
            const string src = "using Microsoft.Win32;\npublic class A { public object M() => Registry.CurrentUser; }";
            Assert.Contains("Microsoft.Win32.Registry", WidgetCompiler.SandboxErrors(src));
        }

        [Theory]
        [InlineData("SetForegroundWindow")]
        [InlineData("SendMessage")]
        [InlineData("keybd_event")]
        [InlineData("registry")]
        [InlineData("BitBlt")]
        public void 字符串字面量里出现这些词不再被拦(string word)
        {
            // 裸子串规则会把说明文字/UI 文案/注释一起拦掉 —— 这是纯误报（改前这几个都不为空）
            var src = "public class A { public string Label => \"" + word + "\"; }";
            Assert.Equal("", WidgetCompiler.SandboxErrors(src));
        }
    }
}
