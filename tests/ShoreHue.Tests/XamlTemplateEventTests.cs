// ==================== 模板里的事件：静默失效 → 明确拒绝 ====================
//
// 真机 bug（便签芯片点不动、✕ 没反应；剪贴板列表点不动、☆/✕ 也没反应）根因是同一个：
// 生成器用 `root.FindName(...)` 挂处理器，而 DataTemplate 里的元素在**独立命名域** →
// FindName 返回 null → 处理器**静默不挂**（XAML 照样编译通过）。
//
// 现在方言检查直接拒绝这种写法，并给出改法（挂到模板外的容器上做委托）；
// 再加一条扫描全部内置小组件 XAML 的护栏，防止以后又写回去。

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

public class XamlTemplateEventTests
{
    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                              "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    [Fact]
    public void 模板里的事件必须被方言拒绝_否则是静默失效()
    {
        string xaml = $"<UserControl {Ns}><ItemsControl><ItemsControl.ItemTemplate><DataTemplate>" +
                      "<Button Content=\"x\" Click=\"OnHit\"/>" +
                      "</DataTemplate></ItemsControl.ItemTemplate></ItemsControl></UserControl>";

        var errors = WidgetCompiler.CheckXamlDialect(xaml);
        Assert.Contains(errors, e => e.Contains("模板里的事件挂不上") && e.Contains("Click"));
    }

    [Fact]
    public void 模板外的事件照常放行()
    {
        string xaml = $"<UserControl {Ns}><Grid>" +
                      "<Button Content=\"x\" Click=\"OnHit\"/>" +
                      "<ItemsControl><ItemsControl.ItemTemplate><DataTemplate>" +
                      "<TextBlock Text=\"{Binding Name}\"/>" +
                      "</DataTemplate></ItemsControl.ItemTemplate></ItemsControl>" +
                      "</Grid></UserControl>";

        Assert.Empty(WidgetCompiler.CheckXamlDialect(xaml));
    }

    [Fact]
    public void 模板外的委托写法必须放行_这是官方推荐替代方案()
    {
        string xaml = $"<UserControl {Ns}><ItemsControl PreviewMouseDown=\"OnDown\" PreviewMouseUp=\"OnUp\">" +
                      "<ItemsControl.ItemTemplate><DataTemplate>" +
                      "<Border MouseLeftButtonUp=\"NeverAllowed\"/>" +
                      "</DataTemplate></ItemsControl.ItemTemplate></ItemsControl></UserControl>";

        // 模板外的 PreviewMouseDown/Up 放行；模板内那个事件被拒（并给出替代写法）
        var errors = WidgetCompiler.CheckXamlDialect(xaml);
        Assert.Single(errors);
        Assert.Contains("MouseLeftButtonUp", errors[0]);
    }

    [Fact]
    public void 内置小组件里不得再有模板内事件()
    {
        string root = RepoRoot();
        var files = Directory.GetFiles(Path.Combine(root, "seabed", "小组件"), "*.xaml", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        foreach (var f in files)
        {
            string xaml = File.ReadAllText(f);
            foreach (Match tpl in Regex.Matches(xaml, "(?s)<DataTemplate>.*?</DataTemplate>"))
            {
                var ev = Regex.Matches(tpl.Value,
                    "(Click|MouseDown|MouseUp|MouseLeftButtonDown|MouseLeftButtonUp|PreviewMouseDown|PreviewMouseUp|" +
                    "Checked|Unchecked|SelectionChanged|ValueChanged|KeyDown|KeyUp|TextChanged|GotFocus|LostFocus|Drop|DragOver)" +
                    "=\"[A-Za-z0-9_]+\"");
                Assert.True(ev.Count == 0,
                    $"{Path.GetFileName(f)} 的 DataTemplate 里还挂着事件（挂不上、点了没反应）：" +
                    string.Join("、", ev.Select(x => x.Value)));
            }
            // 顺带：改完之后方言检查必须干净
            Assert.Empty(WidgetCompiler.CheckXamlDialect(xaml));
        }
    }

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
    // ==================== 根元素上的事件：第二例静默失效 ====================
    //
    // 真机经历：计算器的键盘输入、便签的快捷键都写在根 <UserControl> 上 —— XAML 编译通过、
    // 方言通过、加载正常，但**处理器根本没接上**（生成器按控件名 FindName 找宿主，根元素这条路不通）。
    // 现象就是"敲键盘没反应"。现在方言直接拒绝，并给出改法。

    [Fact]
    public void 根元素上的事件必须被方言拒绝_它会静默失效()
    {
        string xaml = $"<UserControl {Ns} KeyDown=\"Root_KeyDown\"><Grid/></UserControl>";
        var errors = WidgetCompiler.CheckXamlDialect(xaml);
        Assert.Contains(errors, e => e.Contains("根元素上的事件") && e.Contains("KeyDown"));
    }

    [Fact]
    public void 挪到内层带名字的容器上就放行_这是官方推荐改法()
    {
        string xaml = $"<UserControl {Ns}><Grid x:Name=\"RootGrid\" Focusable=\"True\" KeyDown=\"Root_KeyDown\"/></UserControl>";
        Assert.Empty(WidgetCompiler.CheckXamlDialect(xaml));
    }
}
