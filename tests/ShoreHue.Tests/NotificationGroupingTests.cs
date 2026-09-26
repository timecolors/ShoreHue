using System.Collections.Generic;
using System.Linq;
using ShoreHue.Builtin.Notifications;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 通知坞「按应用分组 / 单条关闭」的纯逻辑自测（不碰 UI、不碰宿主静态状态）。
/// 面板只负责把这里的结果画出来，所以这些断言就是"分组与关闭真的对"的证据。
/// </summary>
public class NotificationGroupingTests
{
    private static NotificationRow R(string key, string app, string msg = "", string time = "10:00")
        => new(key, app, msg, time);

    [Fact]
    public void 分组_按应用_组内保序_组间按首次出现()
    {
        var rows = new[] { R("1", "系统"), R("2", "微信"), R("3", "系统") };
        var groups = NotificationGrouping.Group(rows);

        Assert.Equal(2, groups.Count);
        Assert.Equal("系统", groups[0].App);
        Assert.Equal(new[] { "1", "3" }, groups[0].Rows.Select(r => r.Key));
        Assert.Equal("微信", groups[1].App);
        Assert.Equal(new[] { "2" }, groups[1].Rows.Select(r => r.Key));
    }

    [Fact]
    public void 分组_空应用名归入其他()
    {
        var groups = NotificationGrouping.Group(new[] { R("1", ""), R("2", "   ") });
        Assert.Single(groups);
        Assert.Equal("其他", groups[0].App);
        Assert.Equal(2, groups[0].Rows.Count);
    }

    [Fact]
    public void 分组_过滤已关闭的条目()
    {
        var dismissed = new HashSet<string> { "2" };
        var groups = NotificationGrouping.Group(new[] { R("1", "A"), R("2", "A"), R("3", "A") }, dismissed);
        Assert.Single(groups);
        Assert.Equal(new[] { "1", "3" }, groups[0].Rows.Select(r => r.Key));
    }

    [Fact]
    public void 关闭一条_只关那一条_重复关闭返回false()
    {
        var dismissed = new HashSet<string>();

        Assert.True(NotificationGrouping.Dismiss(dismissed, R("2", "A")));
        Assert.False(NotificationGrouping.Dismiss(dismissed, R("2", "A")));   // 重复关闭不再重绘

        var rest = NotificationGrouping.Group(new[] { R("1", "A"), R("2", "A"), R("3", "A") }, dismissed)
            .Single().Rows.Select(r => r.Key).ToArray();
        Assert.Equal(new[] { "1", "3" }, rest);          // 只关了这一条
        Assert.DoesNotContain("1", dismissed);
        Assert.DoesNotContain("3", dismissed);
    }

    [Fact]
    public void 关闭_空Key不记()
    {
        var dismissed = new HashSet<string>();
        Assert.False(NotificationGrouping.Dismiss(dismissed, R("", "A")));
        Assert.Empty(dismissed);
    }

    [Fact]
    public void 淘汰失效标记_已不在通知里的键被清掉()
    {
        var dismissed = new HashSet<string> { "1", "9" };   // 9 已不在当前通知里
        NotificationGrouping.PruneDismissed(dismissed, new[] { R("1", "A"), R("2", "A") });
        Assert.Contains("1", dismissed);
        Assert.DoesNotContain("9", dismissed);
    }

    [Fact]
    public void 投影行_空字段兜底且Key稳定可复现()
    {
        var a = NotificationGrouping.Row(null, " 你好 ", null);
        Assert.Equal("系统", a.App);
        Assert.Equal("你好", a.Message);

        var b = NotificationGrouping.Row(null, " 你好 ", null);
        Assert.Equal(a.Key, b.Key);                                        // 同内容稳定
        Assert.NotEqual(NotificationGrouping.Row("微信", "你好", null).Key, a.Key);   // 不同应用不撞
    }
}
