using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ShoreHue.UI.AI
{
    /// <summary>
    /// 极简 Markdown → FlowDocument 渲染器（聊天用）：
    /// 支持标题 / 粗体 / 斜体 / 行内代码 / 代码块 / 列表 / 引用 / 链接。
    /// 不引入第三方依赖，样式贴合深色面板。
    /// 行内样式（粗体/斜体/行内代码/链接）通过 Inline 元素实现，可嵌套在标题/列表/引用/段落中。
    /// </summary>
    public static class MiniMarkdown
    {
        private static readonly SolidColorBrush CodeBg = new(Color.FromRgb(45, 45, 55));
        private static readonly SolidColorBrush CodeFg = new(Color.FromRgb(224, 224, 235));
        private static readonly SolidColorBrush TextFg = new(Color.FromRgb(238, 238, 238));
        private static readonly SolidColorBrush QuoteFg = new(Color.FromRgb(160, 160, 170));
        private static readonly SolidColorBrush LinkFg = new(Color.FromRgb(96, 168, 255));

        /// <summary>
        /// 行内标记匹配正则（按优先级排列）：
        ///   1. 粗斜体  ***x***
        ///   2. 行内代码  `x`
        ///   3. 粗体  **x** / __x__
        ///   4. 斜体  *x* / _x_
        ///   5. 链接  [x](url)
        /// 分隔符内不允许出现同类分隔符（[^*] 等），避免贪婪匹配跨段。
        /// </summary>
        private static readonly Regex InlineToken = new(
            @"(\*\*\*[^*]+\*\*\*)|" +
            @"(`[^`]+`)|" +
            @"(\*\*[^*]+\*\*)|(__[^_]+__)|" +
            @"(\*[^*]+\*)|(_[^_]+_)|" +
            @"(\[[^\[\]]+\]\([^)\s]+\))",
            RegexOptions.Compiled);

        public static FlowDocument ToFlowDocument(string markdown)
        {
            var doc = new FlowDocument
            {
                FontSize = 13,
                Foreground = TextFg,
                PagePadding = new Thickness(4),
                LineHeight = 1.35
            };

            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            bool inCodeBlock = false;
            var codeLines = new List<string>();

            void FlushCode()
            {
                if (codeLines.Count == 0) return;
                var p = new Paragraph
                {
                    Background = CodeBg,
                    Margin = new Thickness(0, 4, 0, 6),
                    Padding = new Thickness(8, 6, 8, 6)
                };
                p.Inlines.Add(new Run(string.Join("\n", codeLines))
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = CodeFg
                });
                doc.Blocks.Add(p);
                codeLines.Clear();
            }

            foreach (var raw in lines)
            {
                string line = raw.TrimEnd();

                if (inCodeBlock)
                {
                    if (line.TrimStart().StartsWith("```"))
                    {
                        inCodeBlock = false;
                        FlushCode();
                    }
                    else
                    {
                        codeLines.Add(line);
                    }
                    continue;
                }

                if (line.TrimStart().StartsWith("```"))
                {
                    FlushCode();
                    inCodeBlock = true;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line)) continue;

                // 标题
                var m = Regex.Match(line, "^(#{1,4})\\s+(.*)$");
                if (m.Success)
                {
                    int level = m.Groups[1].Value.Length;
                    var hp = new Paragraph
                    {
                        Margin = new Thickness(0, 4, 0, 4),
                        FontSize = level <= 1 ? 17 : level == 2 ? 15 : 14,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = TextFg
                    };
                    AddInlines(hp, m.Groups[2].Value);
                    doc.Blocks.Add(hp);
                    continue;
                }

                // 引用
                if (line.StartsWith(">"))
                {
                    var qp = new Paragraph { Margin = new Thickness(6, 1, 0, 3) };
                    var quoteText = line.TrimStart('>').Trim();
                    if (quoteText.Length == 0)
                    {
                        // 空引用行：保留一个占位 Run，避免段落被折叠
                        qp.Inlines.Add(new Run(" "));
                    }
                    else
                    {
                        // 引用整体斜体灰色；行内标记在引用里也解析
                        var span = new Span { Foreground = QuoteFg, FontStyle = FontStyles.Italic };
                        AddInlines(span, quoteText);
                        qp.Inlines.Add(span);
                    }
                    doc.Blocks.Add(qp);
                    continue;
                }

                // 无序列表
                var bullet = Regex.Match(line, "^[-*+]\\s+(.*)$");
                if (bullet.Success)
                {
                    var lp = new Paragraph
                    {
                        Margin = new Thickness(14, 1, 0, 2),
                        TextIndent = -10
                    };
                    lp.Inlines.Add(new Run("•  "));
                    AddInlines(lp, bullet.Groups[1].Value);
                    doc.Blocks.Add(lp);
                    continue;
                }

                // 有序列表
                var num = Regex.Match(line, "^(\\d+)[.)]\\s+(.*)$");
                if (num.Success)
                {
                    var np = new Paragraph
                    {
                        Margin = new Thickness(14, 1, 0, 2),
                        TextIndent = -12
                    };
                    np.Inlines.Add(new Run(num.Groups[1].Value + ".  "));
                    AddInlines(np, num.Groups[2].Value);
                    doc.Blocks.Add(np);
                    continue;
                }

                // 普通段落
                var pp = new Paragraph { Margin = new Thickness(0, 1, 0, 3) };
                AddInlines(pp, line);
                doc.Blocks.Add(pp);
            }

            if (inCodeBlock) FlushCode();
            return doc;
        }

        /// <summary>
        /// 把一行文本解析为行内 Inline 序列（粗体/斜体/行内代码/链接），追加到 target。
        /// 文本中不含任何行内标记时只创建一个 Run（与原实现一致）。
        /// </summary>
        private static void AddInlines(TextElement target, string text)
        {
            // Paragraph / Span 均有 Inlines 集合（TextElement 基类没有）
            System.Windows.Documents.InlineCollection inlines = target switch
            {
                Paragraph p => p.Inlines,
                Span s => s.Inlines,
                _ => throw new ArgumentException("不支持的 TextElement 类型: " + target.GetType().Name)
            };

            if (string.IsNullOrEmpty(text))
            {
                inlines.Add(new Run(""));
                return;
            }

            var matches = InlineToken.Matches(text);
            if (matches.Count == 0)
            {
                inlines.Add(new Run(text));
                return;
            }

            int pos = 0;
            foreach (Match match in matches)
            {
                // 匹配前的普通文本
                if (match.Index > pos)
                {
                    inlines.Add(new Run(text.Substring(pos, match.Index - pos)));
                }

                Inline? inline = BuildInline(match);
                if (inline != null)
                {
                    inlines.Add(inline);
                }
                else
                {
                    inlines.Add(new Run(match.Value));
                }

                pos = match.Index + match.Length;
            }

            // 末尾剩余普通文本
            if (pos < text.Length)
            {
                inlines.Add(new Run(text.Substring(pos)));
            }
        }

        private static Inline? BuildInline(Match match)
        {
            string value = match.Value;

            // 行内代码 `x`
            if (value.Length >= 2 && value[0] == '`' && value[^1] == '`')
            {
                return new Span(new Run(value.Substring(1, value.Length - 2)))
                {
                    Background = CodeBg,
                    Foreground = CodeFg,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12
                };
            }

            // 链接 [text](url)
            var link = Regex.Match(value, "^\\[([^\\[\\]]+)\\]\\(([^)\\s]+)\\)$");
            if (link.Success)
            {
                string url = link.Groups[2].Value;
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                {
                    var hl = new Hyperlink(new Run(link.Groups[1].Value))
                    {
                        NavigateUri = uri,
                        Foreground = LinkFg
                    };
                    // 聊天内点击链接 → 系统默认浏览器打开
                    hl.RequestNavigate += (s, e) =>
                    {
                        try
                        {
                            // ★ 安全：仅 http/https 协议放行，拦截 file:///、ms-msdt:、shell:、javascript: 等协议注入
                            //   （AI 输出内容不可信，恶意链接可诱导执行本地程序）
                            if (e.Uri != null && (e.Uri.Scheme == Uri.UriSchemeHttp || e.Uri.Scheme == Uri.UriSchemeHttps))
                            {
                                Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
                            }
                        }
                        catch (Exception ex)
                        {
                            // 用户点了 Markdown 里的链接却没打开浏览器（低频，便于排查）
                            ShoreHue.Core.Infrastructure.Logging.LogManager.Debug($"[Markdown] 打开链接失败：{ex.Message}");
                        }
                        e.Handled = true;
                    };
                    return hl;
                }
                return null;
            }

            // 粗斜体 ***x***
            if (value.Length >= 6 && value.StartsWith("***", StringComparison.Ordinal) && value.EndsWith("***", StringComparison.Ordinal))
            {
                var inner = value.Substring(3, value.Length - 6);
                var b = new Bold();
                b.Inlines.Add(new Italic(new Run(inner)));
                return b;
            }

            // 粗体 **x** / __x__
            if (value.Length >= 4 &&
                ((value.StartsWith("**", StringComparison.Ordinal) && value.EndsWith("**", StringComparison.Ordinal)) ||
                 (value.StartsWith("__", StringComparison.Ordinal) && value.EndsWith("__", StringComparison.Ordinal))))
            {
                return new Bold(new Run(value.Substring(2, value.Length - 4)));
            }

            // 斜体 *x* / _x_
            if (value.Length >= 2 &&
                ((value[0] == '*' && value[^1] == '*') ||
                 (value[0] == '_' && value[^1] == '_')))
            {
                return new Italic(new Run(value.Substring(1, value.Length - 2)));
            }

            return null;
        }
    }
}