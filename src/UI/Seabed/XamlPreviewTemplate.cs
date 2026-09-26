// ==================== 海床 XAML 预览的「设计期宿主」 ====================
//
// 为什么需要它（实测出来的，不是猜的）：
//   海床的实时预览用 `XamlReader.Parse` 解析面板 XAML，而 **Parse 在解析期看不见 Application.Resources**。
//   于是凡是用 `{StaticResource CardStyle / AccentButton / FlatButton}` 的面板，预览一律报
//   "无法找到名为 … 的资源" → **整块预览失败**；只用 `{DynamicResource}` 的（clipboard/note）反而能出来。
//   实测：calculator / textai / timer 三个失败，clipboard 的 ClipCollapsedHeight 未解析，note 正常。
//
// WPF 自己的设计器就是用"设计期宿主"解决这件事的：把控件放进一个容器，容器资源里**合并真实主题字典**，
// 解析期就能解析 StaticResource —— 这同时解释了为什么运行时（Roslyn 编译出的 InitializeComponent
// 走 Application.LoadComponent）一切正常，只有"裸 Parse 预览"坏掉。
//
// 本类**只做字符串拼装**（无 WPF 依赖、不认识 Application）→ 可被单测钉住；
// 真正的 Parse 由页面负责（SeabedPage.UpdateXamlPreview）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ShoreHue.UI.Seabed
{
    internal static class XamlPreviewTemplate
    {
        /// <summary>设计期占位高度：clipboard 折叠高度 = 默认 4 行 × 16px（与设置里 ClipboardDisplayLength 默认值一致）。</summary>
        internal const int DesignPlaceholder = 64;

        internal static readonly string[] DefaultThemeFiles =
        {
            "src/UI/Theme/Theme.xaml",
            "src/UI/Theme/AppIcons.xaml",
        };

        /// <summary>把相对资源路径变成可跨解析环境使用的 pack URI（`;component` 显式指明程序集）。</summary>
        internal static string PackUri(string assemblyName, string relativePath)
            => "pack://application:,,,/" + assemblyName + ";component/" + (relativePath ?? "").Replace('\\', '/').TrimStart('/');

        /// <summary>默认设计尺寸（拿不到预览框实际宽度时用）。</summary>
        internal const double DefaultDesignWidth = 320;
        internal const double DefaultDesignMinHeight = 160;

        /// <summary>
        /// 造一个"设计期宿主"：外面套一层 Grid，Grid.Resources 合并真实主题字典，
        /// 并为**代码后置在运行时才提供**的 DynamicResource 补设计期占位值（WPF 的 d:DesignHeight 同思路）。
        /// 返回的字符串供 XamlReader.Parse；seededKeys 是补了占位值的键名（供界面提示"这是占位"）。
        /// ★ 认不出根元素时**原样返回**：宁可让 Parse 报它自己的错，也不要拼出一段半成品 XAML 掩盖真实原因。
        /// </summary>
        internal static string Host(string? cleanedXaml, IReadOnlyList<string>? dictionaryUris, out IReadOnlyList<string> seededKeys)
            => Host(cleanedXaml, dictionaryUris, DefaultDesignWidth, DefaultDesignMinHeight, out seededKeys);

        /// <summary>
        /// 同上，但**给宿主一个设计尺寸**（对应 WPF 设计器的 d:DesignWidth / d:DesignHeight）。
        /// 为什么必须有：面板 XAML 基本都写成"撑满型"（`RowDefinition Height="*"`、宽度靠宿主给），
        /// 放进一个不限制尺寸的容器里，它们会塌成一根竖条 —— 实测 note 在无尺寸预览里只有 56×167，
        /// 用户看到的就是"没渲染成功"。给宽度（内容高度仍可自由增长，超出可滚动）之后才像真面板。
        /// </summary>
        internal static string Host(string? cleanedXaml, IReadOnlyList<string>? dictionaryUris,
                                    double designWidth, double designMinHeight, out IReadOnlyList<string> seededKeys)
        {
            var keys = new List<string>();
            seededKeys = keys;
            if (string.IsNullOrWhiteSpace(cleanedXaml)) return cleanedXaml ?? "";

            string body = StripDeclaration(cleanedXaml!);

            var root = Regex.Match(body, "<[A-Za-z0-9_:]+((?:\\s+xmlns[A-Za-z0-9_:]*=\"[^\"]*\")+)");
            if (!root.Success) return body;
            string xmlns = root.Groups[1].Value;

            var sb = new StringBuilder();
            sb.Append("<Grid xmlns:sys=\"clr-namespace:System;assembly=System.Runtime\"").Append(xmlns);
            // 固定宽度 + 最小高度：宽度让"撑满型"布局有参照，高度仍随内容增长（长内容靠外层滚动）
            if (designWidth > 0)
                sb.Append(" Width=\"").Append(designWidth.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('"');
            if (designMinHeight > 0)
                sb.Append(" MinHeight=\"").Append(designMinHeight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('"');
            sb.Append('>');
            sb.Append("<Grid.Resources><ResourceDictionary>");

            // x:Key 需要 x 命名空间；没有 xmlnns:x 的 XAML 不补占位（否则占位本身会变成新的解析错误）
            bool canSeed = xmlns.Contains("xmlns:x=", StringComparison.Ordinal);
            if (canSeed)
            {
                foreach (string key in DynamicResourceKeys(body).Where(IsSizeLike))
                {
                    keys.Add(key);
                    sb.Append("<sys:Double x:Key=\"").Append(key).Append("\">").Append(DesignPlaceholder).Append("</sys:Double>");
                }
            }

            sb.Append("<ResourceDictionary.MergedDictionaries>");
            foreach (string uri in dictionaryUris ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(uri)) continue;
                sb.Append("<ResourceDictionary Source=\"").Append(Escape(uri)).Append("\"/>");
            }
            sb.Append("</ResourceDictionary.MergedDictionaries>");

            sb.Append("</ResourceDictionary></Grid.Resources>");
            sb.Append(body).Append("</Grid>");
            return sb.ToString();
        }

        /// <summary>XAML 里用到的 DynamicResource 键名（去重、保序）。</summary>
        internal static IReadOnlyList<string> DynamicResourceKeys(string? xaml)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(xaml)) return list;
            foreach (Match m in Regex.Matches(xaml!, "\\{DynamicResource\\s+([A-Za-z0-9_.]+)\\}"))
            {
                string k = m.Groups[1].Value;
                if (!list.Contains(k, StringComparer.Ordinal)) list.Add(k);
            }
            return list;
        }

        /// <summary>
        /// 只给"尺寸类"键补占位：名字以 Height/Width/FontSize 结尾 → 补 Double（类型必然对得上）。
        /// 别的键（画刷/样式/模板）**不猜** —— 类型猜错会变成一个比"没渲染"更难懂的错误。
        /// </summary>
        internal static bool IsSizeLike(string key)
            => key.EndsWith("Height", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("Width", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("FontSize", StringComparison.OrdinalIgnoreCase);

        private static string StripDeclaration(string xaml)
            => Regex.Replace(xaml, "^\\s*<\\?xml[^>]*\\?>\\s*", "");

        private static string Escape(string s)
            => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
