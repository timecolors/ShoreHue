using System;
using System.IO;
using System.Linq;
using System.Threading;
using ShoreHue.Infrastructure.Utils;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 编译产物缓存（内存 + 落盘）：面板内容构建是同步跑在 UI 线程上的，插件编译走 Roslyn。
/// 实测首次激活小组件面板：内置件文件编译 1590ms → 面板 1639ms 后才开始显示；
/// 命中落盘缓存后同一项只要 38ms、整体 88ms —— 这一段直接影响"切换面板的手感"。
///
/// 本测试盯三件事：① 落盘缓存确实写出来了；② 缓存坏了/被删了能回落到现编；③ 内容一变程序集名就变（不会拿旧程序集当新内容）。
/// </summary>
[Collection("WidgetStore")]   // ★ 会改 AppPaths.TestDataRoot（编译缓存写在 DataRoot 下），必须串行
public class WidgetCompileCacheTests : IDisposable
{
    private readonly string _dir;

    public WidgetCompileCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sh_cache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AppPaths.TestDataRoot = _dir;
    }

    public void Dispose()
    {
        AppPaths.TestDataRoot = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    private const string Xaml = @"<UserControl xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
<StackPanel><TextBlock x:Name=""Lbl"" Text=""Hi""/></StackPanel>
</UserControl>";

    private static string Cs(string marker) => @"using System.Windows.Controls;
public partial class CacheProbeWidget : System.Windows.Controls.UserControl, ShoreHue.UI.Widgets.IWidget
{
    // " + marker + @"
    public CacheProbeWidget() { InitializeComponent(); }
    public string Name => ""缓存探针"";
    public UserControl CreateView() => this;
    public void OnActivated() { }
    public void OnDeactivated() { }
}";

    private static T RunSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = action(); } catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
        return result;
    }

    private string CacheDir => Path.Combine(_dir, "compiled");

    [Fact]
    public void 编译成功后把产物写进缓存目录()
    {
        var (widget, err) = RunSta(() => WidgetCompiler.CompileXaml("cacheprobe-a", Xaml, Cs("// v1")));
        Assert.True(widget != null, "编译失败: " + err);

        Assert.True(Directory.Exists(CacheDir), "缓存目录没建出来");
        var dlls = Directory.GetFiles(CacheDir, "*.dll");
        Assert.NotEmpty(dlls);
        // 程序集名必须由 id + 内容哈希决定（同名 == 同内容），且是 XAML 形态的前缀
        Assert.Contains(dlls, f => Path.GetFileName(f).StartsWith("ShoreHue.Widget.xaml_cacheprobe-a_", StringComparison.Ordinal));
    }

    [Fact]
    public void 删掉缓存后仍能编译成功_回落到现编()
    {
        var first = RunSta(() => WidgetCompiler.CompileXaml("cacheprobe-b", Xaml, Cs("// v1")));
        Assert.True(first.widget != null, "首次编译失败: " + first.error);

        foreach (var f in Directory.GetFiles(CacheDir, "*.dll")) File.Delete(f);

        var second = RunSta(() => WidgetCompiler.CompileXaml("cacheprobe-c", Xaml, Cs("// v2")));
        Assert.True(second.widget != null, "缓存删除后编译失败: " + second.error);
        // 现编之后应该又把缓存写回来了
        Assert.NotEmpty(Directory.GetFiles(CacheDir, "*.dll"));
    }

    [Fact]
    public void 内容变化时程序集名随之变化_不会拿旧程序集当新内容()
    {
        var a = RunSta(() => WidgetCompiler.CompileXaml("cacheprobe-d", Xaml, Cs("// v1")));
        Assert.True(a.widget != null, "编译失败: " + a.error);
        string name1 = a.widget!.GetType().Assembly.GetName().Name!;

        var b = RunSta(() => WidgetCompiler.CompileXaml("cacheprobe-e", Xaml, Cs("// v2")));
        Assert.True(b.widget != null, "编译失败: " + b.error);
        string name2 = b.widget!.GetType().Assembly.GetName().Name!;

        Assert.NotEqual(name1, name2);
    }

    [Fact]
    public void 预热与真正编译产出同名程序集_否则预热会静默空操作()
    {
        // ★ 这条盯的是"预热是不是白编了"：WarmAssembly 若另起一套命名/形态，就会编出另一份程序集，
        //   真正加载时照样现编 —— 而日志上"预热完成 N/N"一切正常，体感毫无改善。
        //   这是最难发现的那类失效（我做这个预热时最担心的就是它）。
        //   真机证据（2026-09-13）：预热产出的 xaml_builtin_calculator_F2A9114C… / builtin_web_F4938712… 
        //   与 UI 路径先前自己编出来的哈希完全一致。
        Assert.True(WidgetCompiler.WarmAssembly("cacheprobe-w", Xaml, Cs("// warm"), null), "预热应成功");

        var warmed = System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies
            .FirstOrDefault(a => (a.GetName().Name ?? "").StartsWith("ShoreHue.Widget.xaml_cacheprobe-w_", StringComparison.Ordinal));
        Assert.NotNull(warmed);                       // 预热确实编了并载入了一份

        var (widget, err) = RunSta(() => WidgetCompiler.CompileXaml("cacheprobe-w", Xaml, Cs("// warm")));
        Assert.True(widget != null, "编译失败: " + err);
        Assert.Same(warmed, widget!.GetType().Assembly);   // 真正编译复用的就是预热那一份
    }

    [Fact]
    public void 预热失败只返回false绝不抛异常()
    {
        // 预热是纯优化：任何异常都不能冒出去影响启动/唤出面板这条路径。
        Assert.False(WidgetCompiler.WarmAssembly("cacheprobe-bad1", null, null, null));
        Assert.False(WidgetCompiler.WarmAssembly("cacheprobe-bad2", "不是 XAML", "没有 partial class", null));
    }
}
