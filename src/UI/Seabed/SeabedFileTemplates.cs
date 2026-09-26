// ==================== 海床「新建文件」的骨架模板 ====================
//
// 为什么单独成类：海床页（SeabedPage）已经 2500+ 行，再往里塞模板字符串就是往上帝类上继续堆。
// 这里**只依赖字符串**，不碰 UI、不碰文件系统，因此可以直接单测。
//
// 模板的存在意义只有一个：让用户新建出来的文件**马上能用**（能编译、能加载），
// 而不是建完看到一堆报错 —— 所以骨架里连"事件元素必须有 x:Name"这类非显然规则都写在注释里。

namespace ShoreHue.UI.Seabed
{
    internal static class SeabedFileTemplates
    {
        /// <summary>按扩展名给出初始内容（不认识的扩展名给空文件，不做多余的事）。</summary>
        internal static string ForExtension(string ext, string stem) => ext switch
        {
            ".cs" => CsWidget(stem),
            ".xaml" => Xaml(),
            ".json" => "{\n}\n",
            _ => ""
        };

        /// <summary>纯代码小组件骨架：保证**能直接编译通过**（否则不如不建）。</summary>
        internal static string CsWidget(string stem) => string.Join("\n", new[]
        {
            "// 海床 · 纯代码小组件（C# 形态）",
            "// 保存后宿主会现场编译这个文件，并把它作为小组件标签显示在面板里。",
            "// 想改成别的样子，直接把这个文件整份交给 AI，让它照上面的要求改即可。",
            "using System.Windows;",
            "using System.Windows.Controls;",
            "using System.Windows.Media;",
            "",
            "public class " + SafeClassName(stem) + " : UserControl, ShoreHue.UI.Widgets.IWidget",
            "{",
            "    public " + SafeClassName(stem) + "()",
            "    {",
            "        Content = new TextBlock",
            "        {",
            "            Text = \"你好，海岸线\",",
            "            FontSize = 14,",
            "            Foreground = Brushes.White,",
            "            Margin = new Thickness(12)",
            "        };",
            "    }",
            "",
            "    public string Name => \"" + SafeDisplayName(stem) + "\";",
            "    public UserControl CreateView() => this;",
            "    public void OnActivated() { }",
            "    public void OnDeactivated() { }",
            "}",
            ""
        });

        /// <summary>完全编程形态的界面骨架（注释里写明两条非显然规则）。</summary>
        internal static string Xaml() => string.Join("\n", new[]
        {
            "<!-- 海床 · 完全编程形态的界面文件",
            "     两条容易踩的规则：",
            "       ① 文件名必须与所在目录名一致（加载器按 <目录名>.xaml 查找），且需要同名的 <目录名>.xaml.cs；",
            "       ② 事件所在元素必须有 x:Name，否则该事件不会被挂上（按钮点了没反应）。 -->",
            "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"",
            "             xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">",
            "    <StackPanel Margin=\"12\">",
            "        <TextBlock Text=\"你好，海岸线\" FontSize=\"14\" Foreground=\"#EEEEEE\"/>",
            "        <Button x:Name=\"BtnHello\" Content=\"点我\" Margin=\"0,8,0,0\" Padding=\"10,4\" Click=\"BtnHello_Click\"/>",
            "    </StackPanel>",
            "</UserControl>",
            ""
        });

        internal static string XamlCs(string stem) => string.Join("\n", new[]
        {
            "// 海床 · 完全编程形态的代码后置",
            "// 要求：partial class + 实现 IWidget + 构造函数里调用 InitializeComponent()。",
            "// 文件名必须与所在目录名一致（这里生成的是 <目录名>.xaml.cs）。",
            "using System.Windows;",
            "using System.Windows.Controls;",
            "",
            "public partial class " + SafeClassName(stem) + " : UserControl, ShoreHue.UI.Widgets.IWidget",
            "{",
            "    public " + SafeClassName(stem) + "() => InitializeComponent();",
            "",
            "    public string Name => \"" + SafeDisplayName(stem) + "\";",
            "    public UserControl CreateView() => this;",
            "    public void OnActivated() { }",
            "    public void OnDeactivated() { }",
            "",
            "    private void BtnHello_Click(object sender, RoutedEventArgs e)",
            "    {",
            "        // 点按后的行为写在这里",
            "    }",
            "}",
            ""
        });

        /// <summary>把文件名转成合法类名（`main.cs` 这种没人会当类名的，给个中性默认）。</summary>
        internal static string SafeClassName(string stem)
        {
            if (string.IsNullOrWhiteSpace(stem) || stem.Equals("main", System.StringComparison.OrdinalIgnoreCase))
                return "MyWidget";

            var sb = new System.Text.StringBuilder();
            foreach (char c in stem)
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);

            string s = sb.Length == 0 ? "MyWidget" : sb.ToString();
            return char.IsDigit(s[0]) ? "W" + s : s;
        }

        internal static string SafeDisplayName(string stem)
            => string.IsNullOrWhiteSpace(stem) || stem.Equals("main", System.StringComparison.OrdinalIgnoreCase)
                ? "我的小组件"
                : stem;
    }
}
