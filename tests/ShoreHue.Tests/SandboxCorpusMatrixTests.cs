using System;
using System.IO;
using System.Linq;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// ★ 沙箱语料矩阵（能力 × 变体）：
///   · 恶意侧：按攻击者目标分类（数据窃取 / 持久化 / 执行 / 输入注入 / 屏幕捕获 / 网络 / 破坏 / 凭证），
///     每类给多个变体（别名、换行、基类声明、字符串拼接、宿主 API 绕行）；
///   · 良性侧：常见小组件写法 + **真实市场包源码**，用来守误报率（FPR）。
/// 判定入口统一走 WidgetCompiler.SandboxErrors（文本 + 符号 + 数据流 + XAML 标记四路合并）。
/// </summary>
public class SandboxCorpusMatrixTests
{
    private static string Body(string body) =>
        "using System;\nusing System.IO;\nusing System.Net.Http;\nusing System.Windows;\nusing System.Windows.Controls;\n" +
        "public class W : UserControl { public void Run(string path) {\n" + body + "\n} }";

    public static TheoryData<string> Malicious => new()
    {
        // —— 执行 / 进程 ——
        Body("System.Diagnostics.Process.Start(\"calc.exe\");"),
        Body("System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(\"cmd\") { UseShellExecute = true });"),
        Body("var p = System.Diagnostics.Process\n    .Start;"),
        // —— 文件破坏 / 写 ——
        Body("System.IO.File.WriteAllText(path, \"x\");"),
        Body("System.IO.File.AppendAllText(path, \"x\");"),
        Body("System.IO.File.Delete(path);"),
        Body("System.IO.File.Move(path, path + \"2\");"),
        Body("System.IO.File.Replace(path, path + \"2\", null);"),
        Body("System.IO.Directory.CreateDirectory(path);"),
        Body("System.IO.Directory.Delete(path, true);"),
        Body("System.IO.TextWriter w = System.IO.File.CreateText(path); w.Write(1);"),
        Body("System.IO.Stream s = System.IO.File.OpenWrite(path); s.Write(new byte[1], 0, 1);"),
        Body("using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create);"),
        Body("using var bw = new System.IO.BinaryWriter(System.IO.File.Create(path));"),
        Body("System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(path, System.IO.FileMode.Create, \"m\", 10);"),
        Body("System.IO.Compression.ZipFile.ExtractToDirectory(path, path + \"out\");"),
        Body("new System.IO.FileInfo(path).Delete();"),
        Body("new System.IO.DirectoryInfo(path).Create();"),
        // —— 持久化 / 注册表 ——
        Body("Microsoft.Win32.Registry.CurrentUser.OpenSubKey(\"Software\\\\Microsoft\\\\Windows\\\\CurrentVersion\\\\Run\", true);"),
        Body("Microsoft.Win32.Registry.SetValue(\"HKCU\\\\Software\\\\X\", \"y\", 1);"),
        // —— 反射 / 动态执行 ——
        Body("typeof(string).GetMethod(\"ToString\").Invoke(null, null);"),
        Body("var t = System.Type.GetType(\"System.IO.File\");"),
        Body("System.Reflection.Assembly.Load(\"System.Xml\");"),
        Body("System.Activator.CreateInstance(\"System.IO.FileStream\", \"System.IO.FileStream\");"),
        Body("System.Delegate.CreateDelegate(typeof(Action), typeof(string), \"ToString\");"),
        Body("var e = System.Linq.Expressions.Expression.Call(typeof(string), \"ToString\", null, new System.Linq.Expressions.Expression[0]);"),
        Body("System.Runtime.CompilerServices.Unsafe.As<object, string>(ref o);"),
        // —— 原生 / Interop ——
        Body("System.Runtime.InteropServices.Marshal.Copy(System.IntPtr.Zero, new byte[1], 0, 1);"),
        // —— 输入注入 ——
        Body("System.Windows.Forms.SendKeys.SendWait(\"hello\");"),
        // —— 屏幕 / 窗口捕获 ——
        Body("System.Drawing.Graphics.FromImage(new System.Drawing.Bitmap(1, 1)).CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(1, 1));"),
        // —— 网络监听 ——
        Body("new System.Net.HttpListener().Start();"),
        // —— 凭证 / 隐私数据 ——
        Body("var s = ShoreHue.Core.Services.Ai.AiSettingsStore.Load();"),
        Body("System.Security.Cryptography.ProtectedData.Unprotect(new byte[1], null, System.Security.Cryptography.DataProtectionScope.CurrentUser);"),
        Body("ShoreHue.Infrastructure.WinApi.WebFavoriteManager.GetCombined(10);"),
        Body("ShoreHue.Infrastructure.WinApi.RecentAppTracker.GetRecentApps(10);"),
        Body("ShoreHue.Infrastructure.WinApi.SelectedTextCapture.CaptureAsync();"),
        Body("ShoreHue.Infrastructure.WinApi.WindowCaptureService.Capture(System.IntPtr.Zero);"),   // 窗口捕获
        Body("ShoreHue.Infrastructure.WinApi.NotificationCenterReader.Scan();"),   // 宿主通知库
        // —— 宿主破坏 / 系统控制 ——
        Body("ShoreHue.Infrastructure.WinApi.UninstallHelper.LaunchUninstall(true);"),
        Body("ShoreHue.Infrastructure.WinApi.UpdateService.CheckForUpdateAsync();"),
        Body("ShoreHue.Infrastructure.WinApi.WindowAction.Close(System.IntPtr.Zero);"),
        Body("ShoreHue.Infrastructure.WinApi.SystemToast.Show(\"t\", \"m\");"),
        Body("var svc = ShoreHue.Core.Infrastructure.Service.ServiceManager.Instance;"),   // 服务容器
        // —— 数据流外泄（能力组合） ——
        Body("var d = System.IO.File.ReadAllText(path); new HttpClient().PostAsync(\"http://127.0.0.1:1/\", new StringContent(d));"),
        Body("var d = System.IO.File.OpenText(path).ReadToEnd(); new HttpClient().GetAsync(\"http://127.0.0.1:1/?d=\" + d);"),
        Body("var c = System.Windows.Clipboard.GetText(); new HttpClient().GetAsync(\"http://127.0.0.1:1/?d=\" + c);"),
        Body("var r = ShoreHue.UI.Widgets.HostCapabilities.GetRecentItems(5); new HttpClient().GetAsync(\"http://127.0.0.1:1/?d=\" + r.Count);"),
        // —— 能力组合：先读文件、再经字段外发 ——
        "using System.IO; using System.Net.Http; using System.Windows.Controls;\n" +
        "public class W : UserControl { private string _c = \"\"; public void Run(string p) { _c = File.ReadAllText(p); new HttpClient().GetAsync(\"http://127.0.0.1:1/?d=\" + _c); } }",
    };

    public static TheoryData<string> Benign => new()
    {
        Body("var t = new TextBlock { Text = \"hi\" };"),
        Body("var timer = new System.Windows.Threading.DispatcherTimer(); timer.Start();"),
        Body("var sw = System.Diagnostics.Stopwatch.StartNew(); sw.Stop();"),
        Body("new HttpClient().GetAsync(\"https://api.example.com/weather\");"),
        Body("var cfg = System.IO.File.ReadAllText(path); label.Text = cfg;"),
        Body("var cfg = System.IO.File.ReadAllText(path); new HttpClient().GetAsync(\"https://api.example.com/weather\");"),
        Body("System.Windows.Clipboard.SetText(\"x\");"),
        Body("label.Text = System.Text.Json.JsonDocument.Parse(\"{\\\"a\\\":1}\").RootElement.ToString();"),
        Body("ShoreHue.UI.Widgets.HostCapabilities.OpenExternally(\"https://example.com\");"),
        Body("ShoreHue.UI.Widgets.HostCapabilities.ShowToast(\"标题\", \"内容\");"),
        Body("foreach (var it in ShoreHue.UI.Widgets.HostCapabilities.GetRecentItems(5)) label.Text = it.Name;"),
        Body("var s = ShoreHue.UI.Widgets.HostCapabilities.Settings; label.Text = s.UiFontScale.ToString();"),
        Body("var c = ShoreHue.UI.Widgets.HostCapabilities.ClipboardHistory; label.Text = c.History.Count.ToString();"),
    };

    [Theory] [MemberData(nameof(Malicious))]
    public void 恶意样本_必须被拦(string source)
        => Assert.NotEqual("", WidgetCompiler.SandboxErrors(source, ""));

    [Theory] [MemberData(nameof(Benign))]
    public void 良性样本_必须放行(string source)
        => Assert.Equal("", WidgetCompiler.SandboxErrors(source, ""));

    /// <summary>
    /// ★ 误报基线：**仓库里真实的市场包**必须全部放行。
    /// 这是最有说服力的 FPR 语料——它们是真在用的功能，不是为测试编的。
    /// ★ 与客户端/CI 同口径：按**实际编译形态**取文本（XAML 形态看 .xaml.cs + .xaml，纯代码形态看 main.cs），
    ///   以前这里只看 `*.xaml` 的第一份、从不看 .xaml.cs —— 于是"CI 绿但客户端拒装"的不一致没人发现
    ///   （真实案例：textai 包的 .xaml.cs 直连宿主划词 API）。
    /// </summary>
    [Fact]
    public void 真实市场包_全部放行_误报基线()
    {
        string? root = FindRepoRoot();
        Assert.NotNull(root);   // CI 与本地开发都在仓库内运行
        string pkgDir = Path.Combine(root!, "market", "packages");
        int count = 0;
        foreach (var f in Directory.GetFiles(pkgDir, "main.cs", SearchOption.AllDirectories))
        {
            string src = File.ReadAllText(f);
            string dir = Path.GetDirectoryName(f)!;
            var xamlFiles = Directory.GetFiles(dir, "*.xaml");
            var xamlCsFiles = Directory.GetFiles(dir, "*.xaml.cs");
            bool xamlForm = xamlFiles.Length > 0 && xamlCsFiles.Length > 0;
            string xaml = string.Join("\n", xamlFiles.Select(File.ReadAllText));
            string xamlCs = string.Join("\n", xamlCsFiles.Select(File.ReadAllText));

            string err = xamlForm
                ? WidgetCompiler.SandboxErrors(xamlCs, xaml)
                : WidgetCompiler.SandboxErrors(src, "");
            Assert.True(err.Length == 0, Path.GetFileName(dir) + " 被误拦：" + err);
            count++;
        }
        Assert.True(count >= 10, "市场包语料不足，仅找到 " + count + " 个");
    }


    // ============ 6. 受限 XAML 方言（结构白名单）============

    private static string Xaml(string inner) =>
        "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
        "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + inner + "</UserControl>";

    public static TheoryData<string> XamlAllowed => new()
    {
        Xaml("<StackPanel><TextBlock Text=\"你好\" FontSize=\"14\"/><Button x:Name=\"btn\" Content=\"开始\" Click=\"Btn_Click\"/></StackPanel>"),
        Xaml("<Grid><Grid.RowDefinitions><RowDefinition Height=\"Auto\"/><RowDefinition Height=\"*\"/></Grid.RowDefinitions><Border Grid.Row=\"0\" CornerRadius=\"6\" Background=\"#22FFFFFF\"><TextBlock Text=\"卡片\"/></Border></Grid>"),
        Xaml("<StackPanel><TextBlock Text=\"{Binding Name}\"/><StackPanel.Resources><Style TargetType=\"Button\"><Setter Property=\"Height\" Value=\"26\"/></Style></StackPanel.Resources></StackPanel>"),
        Xaml("<Border><Border.Background><SolidColorBrush Color=\"#1E1E1E\"/></Border.Background><TextBlock Text=\"深色卡片\"/></Border>"),
    };

    public static TheoryData<string> XamlRejected => new()
    {
        Xaml("<StackPanel.Resources><ObjectDataProvider x:Key=\"p\" ObjectType=\"{x:Type d:Process}\" MethodName=\"Start\"/></StackPanel.Resources>"),
        Xaml("<x:Code><![CDATA[ void M(){ } ]]></x:Code>"),
        "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:io=\"clr-namespace:System.IO;assembly=System.Runtime\"><TextBlock/></UserControl>",
        Xaml("<MediaElement Source=\"https://example.com/a.mp4\"/>"),
        Xaml("<TextBlock><Hyperlink NavigateUri=\"https://example.com\">点我</Hyperlink></TextBlock>"),
        Xaml("<Image Source=\"file:///C:/Windows/win.ini\"/>"),
        Xaml("<TextBlock Text=\"{x:Static sys:Environment.ExitCode}\"/>"),
        Xaml("<StackPanel><FileSystemWatcher/></StackPanel>"),
    };

    [Theory] [MemberData(nameof(XamlAllowed))]
    public void 良性XAML_放行(string xaml)
        => Assert.Equal("", WidgetCompiler.SandboxErrors("", xaml));

    [Theory] [MemberData(nameof(XamlRejected))]
    public void 危险XAML_拒绝(string xaml)
        => Assert.NotEqual("", WidgetCompiler.SandboxErrors("", xaml));

    private static string? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "market", "packages"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }
}
