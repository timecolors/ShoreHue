// ==================== 便签的"标题 = 第一行"模型 ====================
//
// 这是本轮结构改动的核心：照 Windows 便笺 / Google Keep，**取消独立标题字段**，标题由正文第一行投影而来。
// 投影必须是纯函数（可测），否则"标题与正文不一致"这类 bug 会以别的形式回来。

using System.Collections.Generic;
using ShoreHue.Core.Services;
using Xunit;

namespace ShoreHue.Tests;

public class NoteItemTests
{
    [Fact]
    public void 第一行就是标题_跳过空行并去空白()
    {
        var n = new NoteItem { Content = "\n\n   买菜   \n记得带伞" };
        Assert.Equal("买菜", n.FirstLine);
    }

    [Fact]
    public void 单行内容也是标题()
        => Assert.Equal("这是一条便签", new NoteItem { Content = "这是一条便签" }.FirstLine);

    [Fact]
    public void 空内容没有标题_由界面显示空便签()
    {
        Assert.Equal("", new NoteItem { Content = "" }.FirstLine);
        Assert.Equal("", new NoteItem { Content = "   \n  \t " }.FirstLine);
        Assert.Equal("", new NoteItem { Content = null! }.FirstLine);
    }

    [Fact]
    public void 超长第一行被截断_不撑破芯片()
    {
        var n = new NoteItem { Content = new string('长', 200) };
        Assert.Equal(60, n.FirstLine.Length);
    }

    [Fact]
    public void 悬停预览压成一行并截断()
    {
        var n = new NoteItem { Content = "第一行\n第二行\r\n第三行" };
        Assert.Equal("第一行 第二行 第三行", n.Preview);
        Assert.Equal(201, new NoteItem { Content = new string('x', 500) }.Preview.Length); // 200 + 省略号
    }

    [Fact]
    public void 正文一变_第一行与预览的通知必须一起发()
    {
        // 芯片显示的是 FirstLine，它由 Content 投影而来：只通知 Content 的话，
        // "改第一行即改名"在界面上不成立（标签名不刷新）——这正是上一版的真机 bug 形态。
        var n = new NoteItem();
        var changed = new List<string>();
        n.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        n.Content = "新的第一行";

        Assert.Contains("Content", changed);
        Assert.Contains("FirstLine", changed);
        Assert.Contains("Preview", changed);
    }

    [Fact]
    public void 置顶会发通知_并写入数据()
    {
        var n = new NoteItem();
        bool raised = false;
        n.PropertyChanged += (_, e) => { if (e.PropertyName == "Pinned") raised = true; };

        n.Pinned = true;
        Assert.True(raised);
        Assert.True(n.Pinned);
    }

    [Fact]
    public void 投影属性不参与序列化_免得存成两份真相()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new NoteItem { Content = "甲", Title = "甲" });
        Assert.DoesNotContain("FirstLine", json);
        Assert.DoesNotContain("Preview", json);
        Assert.Contains("\"Title\"", json);      // Title 仍是**存储字段**（旧数据兼容 + 兼容外部读）
        Assert.Contains("\"Pinned\"", json);     // 置顶要落盘
    }
}
