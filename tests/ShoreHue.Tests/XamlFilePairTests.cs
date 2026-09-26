// ==================== 完全编程成对文件：<名字>.xaml + <名字>.xaml.cs ====================
//
// 回归测试钉住的是一个**真机反馈出来的 bug**：点左侧 `.xaml.cs` 时右侧只加载了 .xaml.cs，
// 点 `.xaml` 才两个都加载。根因是 `.xaml.cs` 那支用 GetFileNameWithoutExtension 拼出
// `calculator.xaml.xaml`（该方法只剥掉最后一个扩展名 `.cs`）。

using ShoreHue.UI.Seabed;
using Xunit;

namespace ShoreHue.Tests;

public class XamlFilePairTests
{
    private const string Root = @"C:\seabed\面板\calculator";

    [Fact]
    public void 由xamlcs推出xaml_这是那个bug的核心()
    {
        var (view, code) = XamlFilePair.Resolve(Root + @"\calculator.xaml.cs");
        Assert.Equal(Root + @"\calculator.xaml", view);          // 不是 calculator.xaml.xaml
        Assert.Equal(Root + @"\calculator.xaml.cs", code);
    }

    [Fact]
    public void 由xaml推出xamlcs()
    {
        var (view, code) = XamlFilePair.Resolve(Root + @"\calculator.xaml");
        Assert.Equal(Root + @"\calculator.xaml", view);
        Assert.Equal(Root + @"\calculator.xaml.cs", code);
    }

    [Fact]
    public void 任一半推出来的都是同一对_点哪个都一样()
    {
        var a = XamlFilePair.Resolve(Root + @"\calculator.xaml");
        var b = XamlFilePair.Resolve(Root + @"\calculator.xaml.cs");
        Assert.Equal(a, b);
    }

    [Fact]
    public void 共同名字对两半都是同一个()
    {
        Assert.Equal("calculator", XamlFilePair.Stem(Root + @"\calculator.xaml"));
        Assert.Equal("calculator", XamlFilePair.Stem(Root + @"\calculator.xaml.cs"));
    }

    [Fact]
    public void 大小写不敏感_磁盘上写成大写的也要认()
    {
        var (view, code) = XamlFilePair.Resolve(Root + @"\Calculator.XAML.CS");
        Assert.Equal(Root + @"\Calculator.xaml", view);
        Assert.Equal(Root + @"\Calculator.xaml.cs", code);
    }

    [Fact]
    public void 名字里带点也不受影响()
    {
        // *.xaml.cs 只剥最后那一段 —— 名字本身带点（my.panel）不该被切错
        Assert.Equal("my.panel", XamlFilePair.Stem(Root + @"\my.panel.xaml.cs"));
        Assert.Equal("my.panel", XamlFilePair.Stem(Root + @"\my.panel.xaml"));
    }

    [Fact]
    public void 不是成对文件就返回空_例如main_cs与config()
    {
        Assert.Equal((null, null), XamlFilePair.Resolve(Root + @"\main.cs"));
        Assert.Equal((null, null), XamlFilePair.Resolve(Root + @"\config.json"));
        Assert.Equal((null, null), XamlFilePair.Resolve(Root + @"\manifest.json"));
    }

    [Fact]
    public void 正确区分界面文件与代码后置()
    {
        Assert.True(XamlFilePair.IsView("a.xaml"));
        Assert.False(XamlFilePair.IsView("a.xaml.cs"));       // ★ 关键：.xaml.cs 不是"界面文件"
        Assert.True(XamlFilePair.IsCodeBehind("a.xaml.cs"));
        Assert.False(XamlFilePair.IsCodeBehind("a.xaml"));
        Assert.False(XamlFilePair.IsView("a.cs"));
    }

    [Fact]
    public void 没有目录时也能推出同目录下的另一半()
    {
        var (view, code) = XamlFilePair.Resolve("calculator.xaml.cs");
        Assert.Equal("calculator.xaml", view);
        Assert.Equal("calculator.xaml.cs", code);
    }
}
