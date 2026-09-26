using System.Linq;
using ShoreHue.Core.Services;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 剪贴板**显示文本**不再被无声砍掉（2026-09-13，规则见 docs\设计-自适应尺寸与换行.md）。
///
/// 背景：用户反馈"希望能完整显示内容而不是用省略号"。查下来截断有三处相互叠加：
///   ① `FromText` 里硬编码 500 字 + "..."（真正的元凶）
///   ② `FromHtml`（网页/编辑器复制的富文本走这条）按 200 字砍成 "HTML: 前200字..."
///   ③ 界面模板 `TextTrimming="CharacterEllipsis"`
/// 而且 `ClipboardDisplayLength` 这个"最多显示多少字符"的设置项**从来没有消费者**（纯摆设）。
///
/// 为什么写成断言：这条 bug 的可见形态是"用户看着觉得不全"，靠肉眼对比新旧截图很容易看漏
/// （真机上还踩过一次：历史是持久化的，看到的其实是**改动前存下的老条目**）。
/// </summary>
public class ClipboardDisplayTextTests
{
    [Fact]
    public void 纯文本_不再按500字砍断()
    {
        string text = new string('a', 800);
        var item = ClipboardManager.ClipboardItem.FromText(text);

        Assert.Equal(800, item.DisplayText.Length);          // 旧实现这里会变成 503（500 + "..."）
        Assert.Equal(800, item.FullText!.Length);            // 全文一直是完整的，问题只在显示文本
        Assert.DoesNotContain("...", item.DisplayText);
    }

    [Fact]
    public void 纯文本_只保留病态巨串的安全上限()
    {
        var item = ClipboardManager.ClipboardItem.FromText(new string('a', 5000));

        Assert.Equal(4000 + 3, item.DisplayText.Length);      // 4000 + "..."
        Assert.EndsWith("...", item.DisplayText);
        Assert.Equal(5000, item.FullText!.Length);            // 全文仍然完整，复制回去不受影响
    }

    [Fact]
    public void 富文本_保留分段而不是压成一长行()
    {
        // ★ 这是用户问的"复制了多段内容怎么处理的"：旧实现把 \s+ 一律压成空格，
        //   多段内容糊成一长行，再被 200 字砍掉 —— 既看不出分段、也看不全。
        var item = ClipboardManager.ClipboardItem.FromHtml("<p>第一段</p><p>第二段</p><br/>第三段");

        Assert.Equal("Html", item.Type);
        Assert.Contains("\n", item.DisplayText);
        Assert.Contains("第一段", item.DisplayText);
        Assert.Contains("第三段", item.DisplayText);
        Assert.DoesNotContain("...", item.DisplayText);
        Assert.DoesNotContain("HTML:", item.DisplayText);     // 前缀不再占掉第一行
    }

    [Fact]
    public void 富文本_长内容同样只受安全上限约束()
    {
        string para = new string('b', 300);
        var item = ClipboardManager.ClipboardItem.FromHtml("<p>" + string.Join("</p><p>", Enumerable.Repeat(para, 5)) + "</p>");

        Assert.True(item.DisplayText.Length > 1000, $"富文本也应完整显示，实际只有 {item.DisplayText.Length} 字");
        Assert.DoesNotContain("...", item.DisplayText);
    }
}
