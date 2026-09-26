using System;
using System.IO;
using System.Linq;
using ShoreHue.Infrastructure.Utils;
using ShoreHue.UI.Seabed;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 内置件「文件化」回归（2026-09 试点：timer）。
///
/// 背景：内置小组件历史上**编译进 exe、由宿主 new 出来**，文件夹里那份带 system:true 只作展示 ——
/// 于是「在文件夹里改内置小组件」根本不生效（与「文件夹即真相源」相反）。
/// 迁移做法：把 id 加进 `WidgetPluginStore.MigratedBuiltinIds` → 加载器不再跳过它 →
/// WidgetSwitcher 优先用文件夹里编译出来的实例；文件坏了则回退 exe 内置实现。
///
/// 本测试盯三件事：① 迁移清单里的内置件确实被当作插件加载；② **加载到的内容与文件逐字一致**；
/// ③ 它的 XAML 形态能被独立编译出实例（即 WidgetSwitcher 那条替换路径走得通）。
/// </summary>
[Collection("WidgetStore")]   // ★ 会改 AppPaths.TestDataRoot，必须串行（见 WidgetStoreCollection 注释）
public class BuiltinMigrationTests : IDisposable
{
    private readonly string _dir;

    public BuiltinMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sh_builtin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AppPaths.TestDataRoot = _dir;
        BuiltinTemplateSeeder.Seed();   // 铺出内置件的文件夹镜像（与真实首次运行同一条路径）
        WidgetPluginStore.Reload();
    }

    public void Dispose()
    {
        AppPaths.TestDataRoot = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void 迁移清单_非空且都是合法id()
    {
        Assert.NotEmpty(WidgetPluginStore.MigratedBuiltinIds);
        Assert.All(WidgetPluginStore.MigratedBuiltinIds, id => Assert.True(WidgetPluginStore.IsValidId(id)));
    }

    [Fact]
    public void 迁移的内置件_被当作插件加载且标记为内置()
    {
        foreach (var id in WidgetPluginStore.MigratedBuiltinIds)
        {
            var p = WidgetPluginStore.GetById(id);
            Assert.NotNull(p);
            Assert.True(p!.IsBuiltin, "迁移的内置件应带 IsBuiltin 标记：" + id);
            Assert.Equal("Widget", p.Kind);
        }
    }

    [Fact]
    public void 加载到的内容_与文件夹里的文件逐字一致()
    {
        // ★ 这条就是「文件夹即真相源」的可执行定义：跑的那份 == 文件里那份。
        //   两种形态都要认：XAML 形态读 <id>.xaml/<id>.xaml.cs，纯 C# 单文件形态读 main.cs。
        foreach (var id in WidgetPluginStore.MigratedBuiltinIds)
        {
            var p = WidgetPluginStore.GetById(id)!;
            // ★ 扁平化后：面板都在 面板/<id>/（不再有 面板/小组件/ 这一层）
            string dir = Path.Combine(WidgetPluginStore.RootDir, "面板", id);
            if (!string.IsNullOrEmpty(p.Xaml) || !string.IsNullOrEmpty(p.XamlCs))
            {
                Assert.Equal(File.ReadAllText(Path.Combine(dir, id + ".xaml")), p.Xaml);
                Assert.Equal(File.ReadAllText(Path.Combine(dir, id + ".xaml.cs")), p.XamlCs);
            }
            else
            {
                Assert.Equal(File.ReadAllText(Path.Combine(dir, "main.cs")), p.Source);
            }
        }
    }

    [Fact]
    public void 迁移的内置件_都有可加载形态且XAML形态能过宿主方言校验()
    {
        // ★ 这条盯的是**加载器的前置条件**：ApplyBuiltinFileOverrides 只认
        //   「XAML 形态（.xaml + .xaml.cs）」或「纯 C# 形态（main.cs）」。
        //   旧实现只认前者 → web（模板是单文件 WebViewWidget.cs）永远回退 exe 内置实现，
        //   "在文件夹里改内置件即生效"对它是假的，而日志里只有一行 WRN、界面上毫无异常。
        //   加 web 进 MigratedBuiltinIds 时，本文件原先写死"必须是 XAML 形态"的两条断言先红了 ——
        //   这才暴露出 web 的模板是单文件 WebViewWidget.cs，而加载器当时只认 XAML 形态。
        //   ★ 注意本文件**测不到加载器本身**（ApplyBuiltinFileOverrides 需要 WPF/WidgetSwitcher，
        //     见下面"不做实例化"的理由）：这里只保证"商店给出的数据两种形态都认"，
        //     加载器真的两种都编译，靠真机日志 `[内置件] web 已从 seabed 文件夹加载` 证明。
        //
        // ★ 为什么这里**不做实例化**：内置件的 XAML 里有 {StaticResource Win11Button} 这类宿主主题资源，
        //   测试进程没有 Application/主题字典 → 实例化会因"资源找不到"抛构造异常（与代码无关）。
        //   要真实例化就得在测试进程里建 Application + 合并 Theme.xaml（市场校验器就是这么干的），
        //   但那会把一个**已死的 Dispatcher** 留在 Application.Current 上，让后面依赖
        //   Dispatcher 封送的测试变成偶发假红（本项目刚为此修过一轮）—— 不值得。
        //   实例化那一步由**真机运行**验证：日志里 `[内置件] <id> 已从 seabed 文件夹加载` 就是它。
        foreach (var id in WidgetPluginStore.MigratedBuiltinIds)
        {
            var p = WidgetPluginStore.GetById(id)!;
            bool xamlForm = !string.IsNullOrEmpty(p.Xaml) && !string.IsNullOrEmpty(p.XamlCs);
            bool csForm = !string.IsNullOrEmpty(p.Source);
            Assert.True(xamlForm || csForm,
                $"迁移的内置件必须在文件夹里有可加载形态（.xaml+.xaml.cs 或 main.cs），否则永远回退 exe 实现：{id}");
            // XAML 形态还要过宿主自己的受限方言校验（纯 C# 形态没有方言可校验）
            if (xamlForm) Assert.Empty(WidgetCompiler.CheckXamlDialect(p.Xaml));
        }
    }
}
