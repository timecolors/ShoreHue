using System;
using System.IO;
using System.Linq;
using ShoreHue.UI.Seabed;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 海床面板**扁平化迁移**（2026-09-13，方案见 docs\方案-海床目录扁平化.md）。
///
/// 为什么必须有断言：这是**会移动用户真实文件**的操作。三条自我保护必须被钉住 ——
/// 只移动不删除、目标已存在则跳过（不覆盖）、失败不阻塞。没有这些断言，一次手滑就是用户数据。
/// </summary>
public class SeabedPanelLayoutTests : IDisposable
{
    private readonly string _root;

    public SeabedPanelLayoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sh_layout_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    private string Dir(params string[] parts)
    {
        string p = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    private void Code(string dir, string file = "main.cs")
        => System.IO.File.WriteAllText(Path.Combine(dir, file), "// w");

    // ==================== 迁移 ====================

    [Fact]
    public void 把小组件与面板功能上提到面板一层()
    {
        var widget = Dir("面板", "小组件", "calculator");
        Code(widget, "calculator.xaml");
        var panel = Dir("面板", "面板功能", "panel-ai");
        Code(panel);

        var r = SeabedPanelLayout.Flatten(_root);

        Assert.Equal(2, r.Moved);
        Assert.True(Directory.Exists(Path.Combine(_root, "面板", "calculator")));
        Assert.True(Directory.Exists(Path.Combine(_root, "面板", "panel-ai")));
        Assert.False(Directory.Exists(Path.Combine(_root, "面板", "小组件")));      // 旧分组目录已被清掉
        Assert.False(Directory.Exists(Path.Combine(_root, "面板", "面板功能")));
        // 内容要跟着走（不是新建空目录）
        Assert.True(File.Exists(Path.Combine(_root, "面板", "calculator", "calculator.xaml")));
        Assert.True(File.Exists(Path.Combine(_root, "面板", "panel-ai", "main.cs")));
    }

    [Fact]
    public void 目标已存在则跳过_绝不覆盖()
    {
        var legacy = Dir("面板", "小组件", "calculator");
        Code(legacy, "旧.cs");
        var existing = Dir("面板", "calculator");          // 用户手工整理过 / 上次搬过
        Code(existing, "新.cs");

        var r = SeabedPanelLayout.Flatten(_root);

        Assert.Equal(0, r.Moved);
        Assert.Equal(1, r.Skipped);
        Assert.True(File.Exists(Path.Combine(existing, "新.cs")));                    // 目标内容没被动
        Assert.True(File.Exists(Path.Combine(legacy, "旧.cs")));                      // 源也没被删（留着让人自己看）
    }

    [Fact]
    public void 可重复执行_第二次什么也不做()
    {
        Code(Dir("面板", "小组件", "a"));

        Assert.Equal(1, SeabedPanelLayout.Flatten(_root).Moved);
        var second = SeabedPanelLayout.Flatten(_root);

        Assert.Equal(0, second.Moved);
        Assert.Equal(0, second.Skipped);
        Assert.Equal(0, second.Failed);
    }

    [Fact]
    public void 没有旧分组目录时_什么都不做()
    {
        Dir("面板", "calculator");
        Dir("区域", "触发行为");

        var r = SeabedPanelLayout.Flatten(_root);

        Assert.Equal(new SeabedPanelLayout.FlattenResult(0, 0, 0), r);
    }

    [Fact]
    public void 根目录不存在时不抛()
        => Assert.Equal(0, SeabedPanelLayout.Flatten(Path.Combine(_root, "不存在")).Moved);

    [Fact]
    public void 旧分组目录里还有散文件时_不删那个目录()
    {
        var legacy = Dir("面板", "小组件");
        Code(Dir("面板", "小组件", "a"));
        System.IO.File.WriteAllText(Path.Combine(legacy, "note.txt"), "别把我弄丢");

        SeabedPanelLayout.Flatten(_root);

        Assert.True(Directory.Exists(legacy));                                        // 非空 → 保留
        Assert.True(File.Exists(Path.Combine(legacy, "note.txt")));
    }

    // ==================== 判定：这个目录里有没有代码 ====================

    [Theory]
    [InlineData("main.cs")]
    [InlineData("w.xaml")]
    [InlineData("w.xaml.cs")]
    public void 含代码的目录_算面板目录候选(string file)
    {
        var d = Dir("x");
        Code(d, file);
        Assert.True(SeabedPanelLayout.ContainsCode(d));
    }

    [Fact]
    public void 只有配置子目录的组_不算面板目录候选()
    {
        // 这正是 面板/状态栏 的形态：它没有 manifest、也没有代码，只有两个 Config 子目录。
        // 加载器本来就有 `if (!hasMain && !hasXaml) continue;` 跳过它，这条断言把那个前提显式钉住。
        var statusBar = Dir("面板", "状态栏", "显示项");
        System.IO.File.WriteAllText(Path.Combine(statusBar, "config.json"), "{}");

        Assert.False(SeabedPanelLayout.ContainsCode(Dir("面板", "状态栏")));
    }

    [Fact]
    public void 不存在的目录_不算()
        => Assert.False(SeabedPanelLayout.ContainsCode(Path.Combine(_root, "没有这个")));
}
