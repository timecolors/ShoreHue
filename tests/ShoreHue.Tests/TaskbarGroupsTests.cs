using System.Collections.Generic;
using System.Linq;
using ShoreHue.UI.Panels;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>任务栏标签分组的纯逻辑：成组 / 嵌套（潮池规则）/ 解散 / 持久化。</summary>
public class TaskbarGroupsTests
{
    [Fact]
    public void 两个应用_并成一组()
    {
        var defs = new List<TaskbarGroupDef>();
        string? err = TaskbarGroups.Group(defs, "app", @"C:\a.exe", "app", @"C:\b.exe", out var gid);
        Assert.Null(err);
        Assert.Single(defs);
        Assert.Equal(gid, defs[0].Id);
        Assert.Equal(2, defs[0].Members.Count);
    }

    [Fact]
    public void 应用加进已有组_而不是再建一个()
    {
        var defs = new List<TaskbarGroupDef>();
        TaskbarGroups.Group(defs, "app", "a", "app", "b", out var gid);
        string? err = TaskbarGroups.Group(defs, "group", gid, "app", "c", out var gid2);
        Assert.Null(err);
        Assert.Equal(gid, gid2);
        Assert.Single(defs);
        Assert.Equal(3, defs[0].Members.Count);
    }

    [Fact]
    public void 嵌套_第2层允许_第3层禁止()
    {
        var defs = new List<TaskbarGroupDef>();
        TaskbarGroups.Group(defs, "app", "a1", "app", "a2", out var inner);   // 深度 1
        TaskbarGroups.Group(defs, "app", "b1", "app", "b2", out var outer);   // 深度 1

        // 把 inner 放进 outer → outer 深度 2：允许
        string? err = TaskbarGroups.Group(defs, "group", outer, "group", inner, out _);
        Assert.Null(err);
        Assert.Equal(2, TaskbarGroups.Depth(defs, outer));

        // 再套一层 → 深度 3：禁止
        TaskbarGroups.Group(defs, "app", "c1", "app", "c2", out var third);
        string? err2 = TaskbarGroups.Group(defs, "group", third, "group", outer, out _);
        Assert.NotNull(err2);
    }

    [Fact]
    public void 不能把组放进它自己()
    {
        var defs = new List<TaskbarGroupDef>();
        TaskbarGroups.Group(defs, "app", "a", "app", "b", out var gid);
        Assert.NotNull(TaskbarGroups.AddMember(defs, gid, "group", gid));
    }

    [Fact]
    public void 解散组_并把对它的引用清掉()
    {
        var defs = new List<TaskbarGroupDef>();
        TaskbarGroups.Group(defs, "app", "a", "app", "b", out var inner);
        TaskbarGroups.Group(defs, "app", "c", "app", "d", out var outer);
        TaskbarGroups.Group(defs, "group", outer, "group", inner, out _);
        TaskbarGroups.Ungroup(defs, inner);   // 只解散内层
        Assert.DoesNotContain(defs, d => d.Id == inner);
        Assert.DoesNotContain(defs.Single(d => d.Id == outer).Members, m => m.Kind == "group" && m.Key == inner);
    }

    [Fact]
    public void 成员不足两个_自动解散并级联()
    {
        var defs = new List<TaskbarGroupDef>();
        TaskbarGroups.Group(defs, "app", "a", "app", "b", out var gid);
        TaskbarGroups.RemoveMember(defs, gid, "app", "b");
        Assert.Empty(defs);   // 只剩 1 个成员 → 解散
    }

    [Fact]
    public void 序列化往返_且坏JSON不炸()
    {
        var defs = new List<TaskbarGroupDef>();
        TaskbarGroups.Group(defs, "app", @"C:\x.exe", "app", @"C:\y.exe", out _);
        var back = TaskbarGroups.Deserialize(TaskbarGroups.Serialize(defs));
        Assert.Single(back);
        Assert.Equal(2, back[0].Members.Count);
        Assert.Empty(TaskbarGroups.Deserialize("{ 这不是 JSON"));
        Assert.Empty(TaskbarGroups.Deserialize(null));
    }
}