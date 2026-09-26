using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// XAML「事件宿主元素没有 x:Name」的回归（2026-09）。
///
/// 背景：受限 XAML 方言是「XAML → C# 代码生成」，事件只能挂到生成出来的具名字段上
/// （`xxxField.Click += h`）。宿主元素没有 x:Name 时，旧实现**跳过绑定并记一行 WRN** ——
/// 于是事件静默失效：编译零错、界面照常、按钮是死的。
///
/// 真机实测（2026-09-13）：seabed 内置件 calculator 的模板有 54 个事件属性、只有 9 个 x:Name，
/// 一旦改为「从文件夹加载」（MigratedBuiltinIds），50 个事件全部失效；而 market 包里
/// **同一份 XAML** 被手工补过 50 个 `EvtAutoN` 才正常 —— 同一份文件两个副本，一份好一份坏。
///
/// 修法：生成器在提取绑定前，给这类元素**注入**一个确定的名字（AutoNameEventOwners）。
/// 名字是我们自己写进这份 XAML 的，所以不存在"绑到不相干元素上"的风险。
/// </summary>
public class XamlAutoNameTests
{
    private const string Cs =
        "using System.Windows.Controls;\r\n" +
        "namespace T {\r\n" +
        "  public partial class W : UserControl {\r\n" +
        "    private void Clear_Click(object sender, System.Windows.RoutedEventArgs e) { }\r\n" +
        "    private void T_Changed(object sender, TextChangedEventArgs e) { }\r\n" +
        "    private void T_Lost(object sender, System.Windows.RoutedEventArgs e) { }\r\n" +
        "    private void Foo_Click(object sender, System.Windows.RoutedEventArgs e) { }\r\n" +
        "  }\r\n" +
        "}";

    private static string Xaml(string body) =>
        "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">\r\n" +
        body +
        "\r\n</UserControl>";

    [Fact]
    public void 带事件但宿主元素没有xName_会注入名字并把事件绑上()
    {
        string? gen = XamlCodeGenerator.Generate(
            Xaml("  <StackPanel>\r\n    <Button Content=\"C\" Click=\"Clear_Click\"/>\r\n  </StackPanel>"), Cs);

        Assert.NotNull(gen);
        Assert.Contains("EvtAuto0Field.Click += Clear_Click", gen);
    }

    [Fact]
    public void 已有xName的元素_不会被重复注入()
    {
        string? gen = XamlCodeGenerator.Generate(
            Xaml("  <Button x:Name=\"BtnC\" Content=\"C\" Click=\"Clear_Click\"/>"), Cs);

        Assert.NotNull(gen);
        Assert.Contains("BtnCField.Click += Clear_Click", gen);
        Assert.DoesNotContain("EvtAuto", gen!);
    }

    [Fact]
    public void 同一元素上的多个事件_只注入一个名字()
    {
        string? gen = XamlCodeGenerator.Generate(
            Xaml("  <TextBox TextChanged=\"T_Changed\" LostFocus=\"T_Lost\"/>"), Cs);

        Assert.NotNull(gen);
        int distinct = Regex.Matches(gen!, @"EvtAuto\d+").Select(m => m.Value).Distinct().Count();
        Assert.Equal(1, distinct);
        Assert.Contains(".TextChanged += T_Changed", gen!);
        Assert.Contains(".LostFocus += T_Lost", gen!);
    }

    [Fact]
    public void 标签类型不认识时不注入_避免生成CS1061把整个包编译废掉()
    {
        // InferType 兜底是 FrameworkElement，而 FrameworkElement 上没有 Click →
        // `xxxField.Click += …` 会 CS1061 让整个包编译失败、小组件静默消失。
        // 所以保守边界：不认识的标签维持旧行为（跳过 + WRN），不拿"编译失败"换"绑定成功"。
        string? gen = XamlCodeGenerator.Generate(
            Xaml("  <MyCustomThing Click=\"Foo_Click\"/>"), Cs);

        Assert.NotNull(gen);
        Assert.DoesNotContain("EvtAuto", gen!);
    }

    [Fact]
    public void 真实calculator模板_以前被静默丢弃的事件现在都绑上了()
    {
        // ★ 用**真实模板**兜住这个回归：这三个处理器所在的按钮历史上都没有 x:Name，
        //   它们是"文件化之后计算器按钮变哑"的直接证据。模板里 x:Name 再被漏掉，这条会先红。
        string root = RepoRoot();
        string dir = Path.Combine(root, "seabed", "小组件", "calculator");
        string? gen = XamlCodeGenerator.Generate(
            File.ReadAllText(Path.Combine(dir, "CalculatorWidget.xaml")),
            File.ReadAllText(Path.Combine(dir, "CalculatorWidget.xaml.cs")));

        Assert.NotNull(gen);
        Assert.Contains("+= Clear_Click;", gen!);
        Assert.Contains("+= Equals_Click;", gen!);
        Assert.Contains("+= Digit_Click;", gen!);
    }

    /// <summary>测试进程里定位仓库根（含 seabed 目录的那一级）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "seabed")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
