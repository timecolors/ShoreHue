using System;
using System.Collections.Generic;
using System.Text;

namespace ShoreHue.UI.Widgets.Dynamic
{
    /// <summary>
    /// XAML → C# 补全代码生成器（完全编程模式）：
    /// 把 .xaml + .xaml.cs 转成可动态编译的 C#（XamlReader 加载 + 命名元素绑定 + 事件挂钩）。
    /// XAML 以 base64 嵌入避免转义；正则全部用 verbatim 字符串。
    /// </summary>
    public static class XamlCodeGenerator
    {
        private static readonly string[] EventNames =
        {
            "Click", "MouseDown", "MouseUp", "KeyDown", "KeyUp", "TextChanged",
            "SelectionChanged", "MouseLeftButtonDown", "MouseLeftButtonUp",
            "LostFocus", "GotFocus", "PreviewMouseDown", "PreviewMouseUp",
            "Checked", "Unchecked", "ValueChanged", "DragOver", "Drop"
        };

        public static string? Generate(string xaml, string xamlCs)
        {
            if (string.IsNullOrWhiteSpace(xaml) || string.IsNullOrWhiteSpace(xamlCs)) return null;
            if (!xamlCs.Contains("partial class")) return null;

            var nsM = System.Text.RegularExpressions.Regex.Match(xamlCs, @"namespace\s+([\w.]+)");
            string ns = nsM.Success ? nsM.Groups[1].Value : "";

            var classM = System.Text.RegularExpressions.Regex.Match(xamlCs,
                @"(?:public|internal)\s+partial\s+class\s+([A-Za-z_][A-Za-z0-9_]*)");
            string className = classM.Success ? classM.Groups[1].Value : "DynamicXamlWidget";

            // ★ 先补名字再提取：带事件属性、宿主元素却没有 x:Name 的，先注入一个确定的名字。
            //   不补的话下面的 ExtractBindings 会把它们**静默丢弃**（见 AutoNameEventOwners 注释）。
            xaml = AutoNameEventOwners(xaml);
            var bindings = ExtractBindings(xaml);
            var named = ExtractNamedElements(xaml);
            string cleanXaml = CleanXaml(xaml);

            string xamlB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(cleanXaml));
            var sb = new StringBuilder();
            sb.AppendLine("// ===== 自动生成：InitializeComponent（XamlReader 加载 + 绑定） =====");
            if (!string.IsNullOrEmpty(ns)) { sb.AppendLine("namespace " + ns); sb.AppendLine("{"); }
            sb.AppendLine("public partial class " + className);
            sb.AppendLine("{");
            sb.AppendLine("    private void InitializeComponent()");
            sb.AppendLine("    {");
            sb.AppendLine("        var xamlB64 = \"" + xamlB64 + "\";");
            sb.AppendLine("        var xaml = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(xamlB64));");
            sb.AppendLine("        // ★ 动态 XAML：{StaticResource} 依赖真实 App 资源（Theme.xaml 已由 App 加载）；裸 Parse");
            sb.AppendLine("        var root = (System.Windows.Controls.UserControl)System.Windows.Markup.XamlReader.Parse(xaml);");

            sb.AppendLine("        this.Content = ((System.Windows.Controls.UserControl)root).Content;");
            foreach (var kv in named)
            {
                sb.AppendLine("        var " + kv.Key + "Field = (" + kv.Value + ")((System.Windows.FrameworkElement)root).FindName(\"" + kv.Key + "\");");
                sb.AppendLine("        if (" + kv.Key + "Field != null) this." + kv.Key + " = " + kv.Key + "Field;");
            }
            foreach (var (elem, evt, handler) in bindings)
            {
                sb.AppendLine("        if (" + elem + "Field != null) " + elem + "Field." + evt + " += " + handler + ";");
            }
            sb.AppendLine("    }");
            // ★ 命名元素字段声明（由生成器提供，保证代码自包含）
            foreach (var kv in named)
                sb.AppendLine("    private " + kv.Value + " " + kv.Key + ";");
            sb.AppendLine("}");
            if (!string.IsNullOrEmpty(ns)) sb.AppendLine("}");

            return "using System;" + Environment.NewLine + "using System.Windows;" + Environment.NewLine + xamlCs + Environment.NewLine + sb;
        }

        /// <summary>
        /// 找出"哪个元素持有这个事件属性"。
        /// ★ 旧实现取"事件属性**文本上最后一个** x:Name"当作宿主元素 —— 这是错的：
        ///   事件属性属于**它所在的那个元素**，而那个元素可能根本没有 x:Name（很常见：
        ///   `&lt;Border MouseLeftButtonUp="Item_Click"&gt;` 放在列表项模板里）。
        ///   于是事件被绑到了前面某个不相干的命名元素上（而且那个元素的类型不一定有该事件 →
        ///   生成 `xxxField.Checked += …` 直接 CS1061，整个包编译失败、小组件静默消失）。
        /// 正确做法：从事件属性位置向前找**最近的未闭合起始标签**，取它自己的 x:Name。
        /// 该标签没有 x:Name 时返回 null —— 由调用方决定：AutoNameEventOwners 会先尝试注入一个
        /// 确定的名字；仍没名字（标签类型不认识）才由 ExtractBindings 跳过并记诊断。
        /// </summary>
        private static string? FindOwningStartTag(string xaml, int eventAttrIndex, out int ownerStart, out int tagEnd)
        {
            // 从事件属性处向前扫描标签结构：维护"当前所在标签"的起始位置
            int depth = 0;
            ownerStart = -1;
            tagEnd = -1;
            for (int i = eventAttrIndex - 1; i >= 0; i--)
            {
                char c = xaml[i];
                if (c == '>')
                {
                    depth++;
                }
                else if (c == '<')
                {
                    // 结束标签 </X> 只抵消一次
                    if (i + 1 < xaml.Length && xaml[i + 1] == '/')
                    {
                        if (depth == 0) { /* 不该出现：忽略 */ }
                        else depth--;
                        continue;
                    }
                    if (depth == 0) { ownerStart = i; break; }
                    depth--;
                }
            }
            if (ownerStart < 0) return null;

            // 取起始标签文本（到下一个 '>' 为止）
            int end = xaml.IndexOf('>', ownerStart);
            if (end < 0) { ownerStart = -1; return null; }
            tagEnd = end;
            string tag = xaml.Substring(ownerStart, end - ownerStart + 1);

            var m = System.Text.RegularExpressions.Regex.Match(tag, @"x:Name=""([A-Za-z0-9_]+)""");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// 给「带事件属性、但宿主元素没有 x:Name」的元素**注入**一个确定的名字，返回注入后的 XAML。
        ///
        /// ★ 为什么必须注入、而不是像旧实现那样跳过：
        ///   受限 XAML 方言走的是「XAML → C# 代码生成」，事件只能挂在生成出来的具名字段上
        ///   （`xxxField.Click += h`）。宿主元素没有名字 → 这个事件被**静默丢弃**：
        ///   界面照常、编译零错、日志只有一行 WRN，而按钮是死的。
        ///   真机实测：seabed 内置件 calculator 的模板有 54 个事件属性、却只有 9 个 x:Name，
        ///   一旦改为「从文件夹加载」，50 个事件全部失效；而 market 包里**同一份 XAML**
        ///   被手工补过 50 个 `EvtAutoN` 才正常 —— 同一份文件两个副本，一份好一份坏。
        ///
        /// ★ 为什么这不算"猜宿主元素"：名字是我们**亲手写进这份 XAML** 的，随后 XamlReader
        ///   解析的就是这份文本、FindName 拿到的必然就是这个元素。旧实现拒绝绑定是为了避免
        ///   "绑到前面某个不相干的命名元素上"（那会 CS1061 让整个包编译失败）—— 注入之后这个风险不存在。
        ///
        /// ★ 保守边界：只对 InferType **认识**的标签注入。不认识就退化成 FrameworkElement，
        ///   而 `xxxField.Click` 在 FrameworkElement 上不存在 → CS1061 → 整个包编译失败、小组件静默消失。
        ///   这种情况下维持旧行为（跳过 + WRN）：不拿"编译失败"换"绑定成功"。
        /// </summary>
        private static string AutoNameEventOwners(string xaml)
        {
            string evtPattern = string.Join("|", EventNames);
            var re = new System.Text.RegularExpressions.Regex(@"(" + evtPattern + @")=""([A-Za-z0-9_]+)""");
            var matches = re.Matches(xaml);
            if (matches.Count == 0) return xaml;

            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(xaml, @"x:Name=""([A-Za-z0-9_]+)"""))
                used.Add(m.Groups[1].Value);

            // 插入点（标签名之后）→ 生成的名字。用字典按插入点去重：同一元素有多个事件也只注一个名字。
            var inject = new Dictionary<int, string>();
            int counter = 0;
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (FindOwningStartTag(xaml, m.Index, out int start, out _) != null) continue;   // 已有名字，不动
                if (start < 0) continue;

                // 插到标签名之后：`<Button` + ` x:Name="EvtAutoN"` + ` ...`
                int p = start + 1;
                while (p < xaml.Length && (char.IsLetterOrDigit(xaml[p]) || xaml[p] == '_' ||
                                           xaml[p] == '.' || xaml[p] == ':')) p++;
                if (inject.ContainsKey(p)) continue;
                if (InferType(xaml.Substring(start + 1, p - start - 1)) == "System.Windows.FrameworkElement")
                    continue;   // 标签类型不认识 → 维持旧行为，避免生成 CS1061

                string name;
                do { name = "EvtAuto" + counter++; } while (used.Contains(name));
                used.Add(name);
                inject[p] = name;
            }
            if (inject.Count == 0) return xaml;

            var sb = new StringBuilder(xaml);
            var positions = new List<int>(inject.Keys);
            positions.Sort((a, b) => b.CompareTo(a));    // 从后往前插：前面的下标不受影响
            foreach (int p in positions) sb.Insert(p, " x:Name=\"" + inject[p] + "\"");
            return sb.ToString();
        }

        private static List<(string Elem, string Evt, string Handler)> ExtractBindings(string xaml)
        {
            var result = new List<(string, string, string)>();
            string evtPattern = string.Join("|", EventNames);
            var re = new System.Text.RegularExpressions.Regex(@"(" + evtPattern + @")=""([A-Za-z0-9_]+)""");
            int unresolved = 0;
            foreach (System.Text.RegularExpressions.Match m in re.Matches(xaml))
            {
                string? elem = FindOwningStartTag(xaml, m.Index, out _, out _);
                if (elem == null)
                {
                    // ★ 不猜：宿主元素没有 x:Name 就没有可挂的字段。记一条诊断让作者知道要补 x:Name，
                    //   而不是绑到无关元素上产生一个莫名其妙的编译错误。
                    unresolved++;
                    continue;
                }
                result.Add((elem, m.Groups[1].Value, m.Groups[2].Value));
            }
            if (unresolved > 0)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Warning(
                    $"[XAML] 有 {unresolved} 个事件属性所在元素缺少 x:Name，已跳过绑定（请给该元素加 x:Name，否则事件不会触发）");
            }
            return result;
        }

        private static Dictionary<string, string> ExtractNamedElements(string xaml)
        {
            var result = new Dictionary<string, string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                xaml, @"<([A-Za-z0-9_.]+)[^>]*x:Name=""([A-Za-z0-9_]+)"""))
            {
                string name = m.Groups[2].Value;
                if (!result.ContainsKey(name)) result[name] = InferType(m.Groups[1].Value);
            }
            return result;
        }

        /// <summary>公开入口：清洗 XAML（移除事件/x:Class，动态解析可加载）。供海床预览等复用。</summary>
        public static string CleanXamlPublic(string xaml) => CleanXaml(xaml);

        private static string CleanXaml(string xaml)
        {
            string result = xaml;
            string evtPattern = string.Join("|", EventNames);
            // 移除事件属性（verbatim 正则：s 直接写、"" 转义引号）
            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"\s+(" + evtPattern + @")=""[A-Za-z0-9_]+""", "");
            // 移除 x:Class（verbatim）
            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"\s*x:Class=""[^""]*""", "");
            // ★ 动态 XamlReader 解析本地类型：给无 assembly 的 clr-namespace 补 ;assembly=ShoreHue
            //   （编译期 XAML 可省 assembly（默认当前程序集），但 XamlReader.Parse 需显式指定才能解析）
            result = System.Text.RegularExpressions.Regex.Replace(result,
                @"clr-namespace:([\w.]+)(?!;assembly=)", "clr-namespace:$1;assembly=ShoreHue");
            return result;
        }

        private static string InferType(string tag)
        {
            string shortName = tag.Contains('.') ? tag.Substring(tag.LastIndexOf('.') + 1) : tag;
            if (shortName.Contains("<")) shortName = shortName.Substring(0, shortName.IndexOf("<"));
            return shortName switch
            {
                "TextBlock" => "System.Windows.Controls.TextBlock",
                "Button" => "System.Windows.Controls.Button",
                "TextBox" => "System.Windows.Controls.TextBox",
                "Label" => "System.Windows.Controls.Label",
                "StackPanel" => "System.Windows.Controls.StackPanel",
                "Grid" => "System.Windows.Controls.Grid",
                "Border" => "System.Windows.Controls.Border",
                "Image" => "System.Windows.Controls.Image",
                "ComboBox" => "System.Windows.Controls.ComboBox",
                "ListBox" => "System.Windows.Controls.ListBox",
                "CheckBox" => "System.Windows.Controls.CheckBox",
                "Slider" => "System.Windows.Controls.Slider",
                "ProgressBar" => "System.Windows.Controls.ProgressBar",
                "Expander" => "System.Windows.Controls.Expander",
                "Popup" => "System.Windows.Controls.Primitives.Popup",
                "ScrollViewer" => "System.Windows.Controls.ScrollViewer",
                "TabControl" => "System.Windows.Controls.TabControl",
                "WrapPanel" => "System.Windows.Controls.WrapPanel",
                "Canvas" => "System.Windows.Controls.Canvas",
                "DockPanel" => "System.Windows.Controls.DockPanel",
                "UserControl" => "System.Windows.Controls.UserControl",
                // ★ 补全常用控件：缺一个就退化成 FrameworkElement，而 `xxxField.Click += …`
                //   在 FrameworkElement 上不存在 → CS1061 → 整个包编译失败、小组件静默消失。
                "ToggleButton" => "System.Windows.Controls.Primitives.ToggleButton",
                "RepeatButton" => "System.Windows.Controls.Primitives.RepeatButton",
                "RadioButton" => "System.Windows.Controls.RadioButton",
                "ListView" => "System.Windows.Controls.ListView",
                "ListViewItem" => "System.Windows.Controls.ListViewItem",
                "ListBoxItem" => "System.Windows.Controls.ListBoxItem",
                "TreeView" => "System.Windows.Controls.TreeView",
                "TreeViewItem" => "System.Windows.Controls.TreeViewItem",
                "TabItem" => "System.Windows.Controls.TabItem",
                "MenuItem" => "System.Windows.Controls.MenuItem",
                "Menu" => "System.Windows.Controls.Menu",
                "ContextMenu" => "System.Windows.Controls.ContextMenu",
                "GroupBox" => "System.Windows.Controls.GroupBox",
                "ItemsControl" => "System.Windows.Controls.ItemsControl",
                "ContentControl" => "System.Windows.Controls.ContentControl",
                "RichTextBox" => "System.Windows.Controls.RichTextBox",
                "PasswordBox" => "System.Windows.Controls.PasswordBox",
                "Thumb" => "System.Windows.Controls.Primitives.Thumb",
                "Separator" => "System.Windows.Controls.Separator",
                "ToolTip" => "System.Windows.Controls.ToolTip",
                "Viewbox" => "System.Windows.Controls.Viewbox",
                "UniformGrid" => "System.Windows.Controls.Primitives.UniformGrid",
                "DataGrid" => "System.Windows.Controls.DataGrid",
                _ => "System.Windows.FrameworkElement"
            };
        }
    }
}