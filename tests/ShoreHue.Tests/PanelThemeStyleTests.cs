using System.Windows;
using System.Windows.Controls;
using ShoreHue.UI.Seabed;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>文件化的面板模板必须能拿到主题样式（否则按钮退回默认灰按钮，不像 Win11）。</summary>
public class PanelThemeStyleTests
{
    [Fact]
    public void 模板按钮_能拿到主题样式()
    {
        string outcome = UiTestHost.Run(() =>
        {
            var (widget, err) = WidgetCompiler.Compile("itest_theme_notif", BuiltinFeatureSources.Sources["panel-notification"]);
            if (widget == null) return "编译失败：" + err;
            var view = widget.CreateView();
            UiTestHost.Realize(view);
            var clear = UiTestHost.FindVisual<Button>(view, b => (b.Content as string) == "清空");
            if (clear == null) return "找不到清空按钮";
            var expected = Application.Current?.TryFindResource("FlatButton") as Style;
            if (expected == null) return "Application 里没有 FlatButton（主题没合并？）";
            if (clear.Style == null) return "按钮 Style 为 null（面板里 TryFindResource 没解析到）";
            return clear.Style == expected ? "OK" : "Style 不是 FlatButton";
        });
        Assert.Equal("OK", outcome);
    }
}