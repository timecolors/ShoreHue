// ==================== 内置便签：动态加载路径的三道关 + 绑定回归 ====================
//
// 便签是"文件夹即真相源"的内置件：真正跑的是海床里那份（Roslyn 现编译）。
// 所以除了 `dotnet build`（把 seabed 源码编进 exe 那条路），还必须验证**动态那条路**：
//   ① 受限 XAML 方言；② 三层 C# 沙箱；③ 真能编译出程序集。
// 另外钉住一个真机 bug：标签模板里绑了 `{Binding ColorBrush}`，而列表项的数据上下文是 NoteItem
// —— 那个属性根本不存在，于是"颜色永远不显示"，而 XAML 编译不会报错（绑定是运行时解析的）。

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ShoreHue.Core.Services;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

public class BuiltinNoteWidgetTests
{
    /// <summary>从测试程序集往上找项目根（含 ShoreHue.csproj）——与 BuiltinTemplateSeeder.FindSourceRoot 同思路。</summary>
    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "ShoreHue.csproj"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("找不到项目根（ShoreHue.csproj）");
    }

    private static (string Xaml, string Cs) NoteFiles()
    {
        string dir = Path.Combine(RepoRoot(), "seabed", "小组件", "note");
        return (File.ReadAllText(Path.Combine(dir, "NoteWidget.xaml")),
                File.ReadAllText(Path.Combine(dir, "NoteWidget.xaml.cs")));
    }

    [Fact]
    public void 便签XAML必须过受限方言()
    {
        var (xaml, _) = NoteFiles();
        Assert.Empty(WidgetCompiler.CheckXamlDialect(xaml));
    }

    [Fact]
    public void 便签源码必须过三层沙箱()
    {
        var (xaml, cs) = NoteFiles();
        Assert.Empty(WidgetCompiler.SandboxErrors(cs, xaml));
    }

    [Fact]
    public void 便签必须真的能编译成程序集()
    {
        var (xaml, cs) = NoteFiles();
        Assert.True(WidgetCompiler.WarmAssembly("builtin_note_check", xaml, cs, null),
            "内置便签在动态加载路径上编译失败（真机上会表现为「这个小组件加载不了」）");
    }

    [Fact]
    public void 标签模板里的绑定必须绑到NoteItem真实存在的属性()
    {
        var (xaml, _) = NoteFiles();

        // 取 ItemTemplate 段落里的 {Binding X}（跳过带 Source= 的本地化绑定；
        // ★ 必须先剥掉 XML 注释 —— 注释里提到那个错误绑定时，正则会把它当成真绑定，测试自己骗自己）
        string withoutComments = Regex.Replace(xaml, "<!--.*?-->", "", RegexOptions.Singleline);
        var m = Regex.Match(withoutComments, "<ItemsControl\\.ItemTemplate>(.*?)</ItemsControl\\.ItemTemplate>", RegexOptions.Singleline);
        Assert.True(m.Success, "便签 XAML 里应有标签模板");
        string template = m.Groups[1].Value;

        var props = typeof(NoteItem).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                    .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        // 整个绑定表达式一起取（只有属性名的话看不到后面的 Source=，本地化绑定会被误判）
        var bound = Regex.Matches(template, "\\{Binding([^}]*)\\}")
                          .Select(x => x.Groups[1].Value)
                          .Where(v => !v.Contains("Source=", StringComparison.Ordinal))
                          .Select(v => v.Trim().Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
                          .Where(v => v.Length > 0)
                          .Distinct()
                          .ToList();

        Assert.NotEmpty(bound);
        foreach (var name in bound)
            Assert.True(props.Contains(name),
                $"标签模板绑定了 NoteItem 上不存在的属性 '{name}' —— 绑定失败是静默的，界面上只会「什么都没发生」");
    }

    [Fact]
    public void 便签必须是成熟结构_没有独立标题框且带搜索与置顶()
    {
        var (xaml, _) = NoteFiles();
        // 受限方言不接受 PreviewKeyDown / Loaded / MouseDoubleClick（快捷键改用 KeyDown 冒泡）
        Assert.DoesNotContain("PreviewKeyDown", xaml);
        // 内容区以前硬编码 MaxHeight=300（面板矮了溢出、高了浪费，且光标不跟随）
        Assert.DoesNotContain("MaxHeight=\"300\"", xaml);
        // ★ 成熟方案（Windows 便笺 / Google Keep）**没有独立标题字段**：标题就是第一行。
        //   独立的 TitleEditor 是"标题与正文两套真相"的来源，必须消失。
        Assert.DoesNotContain("TitleEditor", xaml);
        // 列表 + 搜索（便笺列表窗口 / Keep 搜索）
        Assert.Contains("SearchBox", xaml);
        // 置顶标记（Keep 的置顶）
        Assert.Contains("{Binding Pinned}", xaml);
        // 芯片显示的是第一行，而不是那个可能过期的 Title 字段
        Assert.Contains("{Binding FirstLine}", xaml);
    }
}
