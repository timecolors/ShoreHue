using ShoreHue.UI.Seabed;
using ShoreHue.UI.Widgets.Dynamic;
using System.IO;
using System.Text;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>验证海床内置源码模板均可编译（新增模板后跑此测试兜底，防止 closing 引号丢失/语法错误）。</summary>
public class BuiltinFeatureSourcesTests
{
    [Theory]
    [InlineData("widget-timer")]
    [InlineData("widget-calculator")]
    [InlineData("widget-textai")]
    [InlineData("widget-clipboard")]
    [InlineData("widget-note")]
    [InlineData("panel-notification")]
    [InlineData("panel-recent")]
    [InlineData("panel-quicksettings")]
    [InlineData("panel-taskbar-feature")]
    [InlineData("panel-ai")]
    [InlineData("panel-apphelper")]
    [InlineData("panel-windowcontrol")]
    public void Template_Compiles(string key)
    {
        Assert.True(BuiltinFeatureSources.Sources.ContainsKey(key), $"模板 {key} 不存在");
        string err = WidgetCompiler.Validate("tpl_" + key, BuiltinFeatureSources.Sources[key]);
        if (err.Length > 0)
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "tpl-errors.txt"), "===== " + key + " =====\n" + err + "\n");
        }
        Assert.Equal("", err);
    }

    [Fact]
    public void AllConfigLeafNodes_GenerateCompilableCode()
    {
        // 方案C完整版：每个纯配置叶子节点都应生成一段完整可执行的配置代码（防树字段与 SettingsData 漂移）
        var root = ShoreHue.UI.Seabed.ConfigTreeBuilder.Build();
        int checkedCount = 0;
        foreach (var c1 in root.Children)
        {
            foreach (var c2 in c1.Children)
            {
                if (c2.Children.Count == 0)
                {
                    // 二级叶子
                    string code = ShoreHue.UI.Settings.Pages.SeabedPage.BuildConfigCode(c2);
                    string err = WidgetCompiler.Validate("cfg_" + c2.Key, code);
                    Assert.Equal("", err);
                    checkedCount++;
                }
                foreach (var c3 in c2.Children)
                {
                    string code = ShoreHue.UI.Settings.Pages.SeabedPage.BuildConfigCode(c3);
                    string err = WidgetCompiler.Validate("cfg_" + c3.Key, code);
                    Assert.Equal("", err);
                    checkedCount++;
                }
            }
        }
        Assert.True(checkedCount >= 20, "叶子节点数异常: " + checkedCount);
    }

    [Fact]
    public void PanelKeys_AllListed()
    {
        Assert.Contains("panel-notification", BuiltinFeatureSources.PanelKeys);
        Assert.Contains("panel-recent", BuiltinFeatureSources.PanelKeys);
        Assert.Contains("panel-quicksettings", BuiltinFeatureSources.PanelKeys);
        Assert.Contains("panel-taskbar-feature", BuiltinFeatureSources.PanelKeys);
        Assert.Contains("panel-ai", BuiltinFeatureSources.PanelKeys);
        Assert.Contains("panel-apphelper", BuiltinFeatureSources.PanelKeys);
        Assert.Contains("panel-windowcontrol", BuiltinFeatureSources.PanelKeys);
    }
}
