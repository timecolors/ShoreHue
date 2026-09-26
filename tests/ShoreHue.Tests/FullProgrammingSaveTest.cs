using ShoreHue.UI.Widgets.Dynamic;
using ShoreHue.UI.Widgets;
using Xunit;
using System;
using System.Threading;
using System.Windows;
using System.IO;

namespace ShoreHue.Tests
{
    [Collection("WidgetStore")]
    public class FullProgrammingSaveTest
    {
        const string Xaml = @"<UserControl xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""><StackPanel><Button Content=""Hi""/></StackPanel></UserControl>";
        const string XamlCs = @"using System.Windows;
using System.Windows.Controls;
using ShoreHue.UI.Widgets;
public partial class TestXamlWidget : UserControl, IWidget
{
    public TestXamlWidget() { InitializeComponent(); }
    public string Name => ""完全编程测试"";
    public UserControl CreateView() => this;
    public void OnActivated() { }
    public void OnDeactivated() { }
}";

        /// <summary>★ 走共享 UI 宿主（同上：一个进程只能有一个 Application）。</summary>
        private static (IWidget? w, string err) RunSta()
            => UiTestHost.Run(() => WidgetCompiler.CompileXaml("test-fullprog", Xaml, XamlCs));
        [Fact]
        public void FullProgramming_XamlCs_Compiles()
        {
            var (w, err) = RunSta();
            Assert.True(w != null, "完全编程（XAML+CS）编译失败: " + err);
            Assert.Equal("完全编程测试", w!.Name);
        }
    }
}
