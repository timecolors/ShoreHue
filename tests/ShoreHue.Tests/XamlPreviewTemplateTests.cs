// ==================== 海床 XAML 预览：设计期宿主 ====================
//
// 这些断言钉住的是**一个实测出来的坑**：裸 XamlReader.Parse 在解析期看不见 Application.Resources，
// 于是用 {StaticResource CardStyle} 的面板预览整块失败（calculator/textai/timer 实测全挂）。
// 修法是给解析套一层宿主容器、把真实主题字典合并进解析期作用域 —— 这里保证那层壳**拼得对**。

using System.Collections.Generic;
using ShoreHue.UI.Seabed;
using Xunit;

namespace ShoreHue.Tests;

public class XamlPreviewTemplateTests
{
    private const string Widget =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n" +
        "             xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
        "  <Button Style=\"{StaticResource AccentButton}\" MaxHeight=\"{DynamicResource ClipCollapsedHeight}\"/>\n" +
        "</UserControl>";

    private static readonly string[] Theme = { "pack://application:,,,/ShoreHue;component/src/UI/Theme/Theme.xaml" };

    [Fact]
    public void 宿主必须把主题字典合并进解析期作用域_否则StaticResource面板预览全挂()
    {
        string host = XamlPreviewTemplate.Host(Widget, Theme, out _);
        Assert.Contains("ResourceDictionary.MergedDictionaries", host);
        Assert.Contains("Source=\"pack://application:,,,/ShoreHue;component/src/UI/Theme/Theme.xaml\"", host);
    }

    [Fact]
    public void 宿主必须保留原始XAML本体与命名空间()
    {
        string host = XamlPreviewTemplate.Host(Widget, Theme, out _);
        Assert.Contains("<UserControl", host);
        Assert.Contains("Style=\"{StaticResource AccentButton}\"", host);
        Assert.Contains("xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"", host);   // 供容器自身复用
        Assert.EndsWith("</Grid>", host);
    }

    [Fact]
    public void 根元素之前的XML声明必须摘掉_否则加壳后不是文档首节点()
    {
        string host = XamlPreviewTemplate.Host(Widget, Theme, out _);
        Assert.DoesNotContain("<?xml", host);            // 声明出现在中间会让解析器直接报错
        Assert.StartsWith("<Grid", host);
    }

    [Fact]
    public void 代码后置才提供的尺寸类资源_补设计期占位值并如实报告()
    {
        string host = XamlPreviewTemplate.Host(Widget, Theme, out var seeded);
        Assert.Contains("ClipCollapsedHeight", seeded);
        Assert.Contains("<sys:Double x:Key=\"ClipCollapsedHeight\">64</sys:Double>", host);
    }

    [Fact]
    public void 非尺寸类资源不猜_类型猜错比没渲染更难懂()
    {
        string xaml = "<UserControl xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
                      "<Border Background=\"{DynamicResource SettingsCardBg}\"/></UserControl>";
        string host = XamlPreviewTemplate.Host(xaml, Theme, out var seeded);
        Assert.Empty(seeded);
        Assert.DoesNotContain("SettingsCardBg\"", host);   // 不做成 x:Key 占位
    }

    [Fact]
    public void 没有xmlns_x时不补占位_否则占位自己变成新的解析错误()
    {
        string xaml = "<UserControl><Border MaxHeight=\"{DynamicResource FooHeight}\"/></UserControl>";
        string host = XamlPreviewTemplate.Host(xaml, Theme, out var seeded);
        Assert.Empty(seeded);
        Assert.Contains("<UserControl>", host);
    }

    [Fact]
    public void 认不出根元素就原样返回_让Parse报它自己的错()
    {
        const string junk = "这不是 XAML";
        Assert.Equal(junk, XamlPreviewTemplate.Host(junk, Theme, out var seeded));
        Assert.Empty(seeded);
    }

    [Fact]
    public void 参数为空不抛异常()
    {
        Assert.Equal("", XamlPreviewTemplate.Host("", Theme, out _));
        Assert.Equal("", XamlPreviewTemplate.Host(null, Theme, out _));      // 返回空串而不是 null：调用方少一个空判断
        Assert.NotNull(XamlPreviewTemplate.Host(Widget, null, out _));        // 没有字典也要能加壳
        Assert.Contains("<Grid", XamlPreviewTemplate.Host(Widget, new List<string> { "", "  " }, out _));   // 空 URI 被跳过
    }

    [Fact]
    public void 字典URI里的XML敏感字符被转义()
    {
        string host = XamlPreviewTemplate.Host(Widget, new[] { "file:///C:/a&b/c\"d.xaml" }, out _);
        Assert.Contains("a&amp;b/c&quot;d.xaml", host);
    }

    [Fact]
    public void 相对主题路径转成带component的绝对packURI()
    {
        Assert.Equal("pack://application:,,,/ShoreHue;component/src/UI/Theme/Theme.xaml",
            XamlPreviewTemplate.PackUri("ShoreHue", "src/UI/Theme/Theme.xaml"));
        Assert.Equal("pack://application:,,,/ShoreHue;component/src/UI/Theme/Theme.xaml",
            XamlPreviewTemplate.PackUri("ShoreHue", @"\src\UI\Theme\Theme.xaml"));   // 反斜杠也要能转
    }

    // ==================== 设计尺寸（撑满型面板不许塌成一根竖条） ====================

    [Fact]
    public void 宿主必须带设计宽度_否则撑满型布局会塌成竖条()
    {
        // 实测：note.xaml（RowDefinition Height="*" + 无固定宽）在无尺寸宿主里只有 56×167，
        // 真机反馈就是"note 还是没成功"。给宽度之后才像真面板。
        string host = XamlPreviewTemplate.Host(Widget, Theme, 420, 160, out _);
        Assert.Contains("Width=\"420\"", host);
        Assert.Contains("MinHeight=\"160\"", host);
        Assert.StartsWith("<Grid", host);
    }

    [Fact]
    public void 默认重载也带设计尺寸_尺寸不写科学计数法()
    {
        string host = XamlPreviewTemplate.Host(Widget, Theme, 320.5, 160, out _);
        Assert.Contains("Width=\"320.5\"", host);          // 用不变文化格式化，不能变成 320,5
    }

    [Fact]
    public void 尺寸为0时不写该属性_交给调用方含义明确()
    {
        string host = XamlPreviewTemplate.Host(Widget, Theme, 0, 0, out _);
        Assert.DoesNotContain("Width=", host);
        Assert.DoesNotContain("MinHeight=", host);
    }
}
