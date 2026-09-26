using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShoreHue.UI.Seabed;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 海床树的扫描器（2026-09-13 从 `SeabedPage` 抽出来之后才有测试）。
///
/// 为什么值得测：树是「文件夹即真相源」的唯一出口 —— 扫描错了，用户就会看到
/// "文件夹里明明有、树里却没有"或反过来。而它原来埋在 2400 行的页面里，只能靠人在界面上点。
/// 抽出 `SeabedTreeScanner` 之后这些都是可复现的断言，**也是接下来"面板平铺"改造的测试台**：
/// "哪个目录算面板、哪个算配置组"必须能钉住，而不是靠眼睛看树。
/// </summary>
public class SeabedTreeScannerTests : IDisposable
{
    private readonly string _root;

    public SeabedTreeScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sh_tree_" + Guid.NewGuid().ToString("N"));
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

    private string File_(string dir, string name, string content = "// x")
    {
        string p = Path.Combine(dir, name);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    private void Manifest(string dir, string json) => System.IO.File.WriteAllText(Path.Combine(dir, "manifest.json"), json);

    private List<FlatNode> Scan(ISet<string>? expanded = null, string? filter = null)
        => SeabedTreeScanner.Scan(_root, expanded ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase), filter);

    // ==================== 基本形态 ====================

    [Fact]
    public void 顶层只列目录_不列根目录下的文件()
    {
        Dir("常规"); Dir("面板");
        File_(_root, "readme.txt");

        var nodes = Scan();

        Assert.Equal(new[] { "常规", "面板" }, nodes.Select(n => n.Display));   // 按名字排序
        Assert.All(nodes, n => Assert.True(n.FsIsDir));
        Assert.All(nodes, n => Assert.Equal(0, n.Level));
    }

    [Fact]
    public void 未展开的目录不列子项_展开后才列()
    {
        var panel = Dir("面板");
        Dir("面板", "小组件");
        File_(panel, "manifest.json", "{}");

        Assert.Single(Scan());                                  // 没展开 → 只有「面板」一行

        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { panel };
        var nodes = Scan(expanded);

        Assert.Contains(nodes, n => n.Display == "小组件");
        Assert.Contains(nodes, n => n.Display == "manifest.json");
    }

    [Fact]
    public void 行层级上限_最多四层_再深的不出现()
    {
        var l1 = Dir("a");
        var l2 = Dir("a", "b");
        var l3 = Dir("a", "b", "c");
        var l4 = Dir("a", "b", "c", "d");
        Dir("a", "b", "c", "d", "e");                            // 第五层：不该出现

        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { l1, l2, l3, l4 };
        var nodes = Scan(expanded);

        // 行层级是 0..3（`level > 3` 才停）——即"最多四层行"，与改造前的页面行为**逐字一致**。
        Assert.Equal(0, nodes.Single(n => n.Display == "a").Level);
        Assert.Equal(1, nodes.Single(n => n.Display == "b").Level);
        Assert.Equal(2, nodes.Single(n => n.Display == "c").Level);
        Assert.Equal(3, nodes.Single(n => n.Display == "d").Level);
        Assert.DoesNotContain(nodes, n => n.Display == "e");     // 第五层被上限挡住
    }

    // ==================== manifest 判定（批次 3 的地基） ====================

    [Fact]
    public void 从manifest读出kind_id与system标记()
    {
        var rootDir = Dir("面板");
        var widget = Dir("面板", "calculator");
        Manifest(widget, "{ \"id\": \"calculator\", \"kind\": \"Widget\", \"system\": true }");
        Dir("面板", "小组件");
        Manifest(rootDir, "{}");

        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootDir };
        var node = Scan(expanded).First(n => n.Display == "calculator");

        Assert.Equal("Widget", node.FsKind);
        Assert.Equal("calculator", node.FsManifestId);
        Assert.True(node.FsIsSystem);
        Assert.False(node.FsIsConfigDir);
        Assert.True(node.HasDelete);
    }

    [Fact]
    public void 配置目录被标记为Config()
    {
        var panel = Dir("面板");
        var appearance = Dir("面板", "外观");
        Manifest(appearance, "{ \"kind\": \"Config\" }");
        Manifest(panel, "{}");

        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { panel };
        var node = Scan(expanded).First(n => n.Display == "外观");

        Assert.True(node.FsIsConfigDir);
    }

    [Theory]
    [InlineData("小组件", "Widget")]
    [InlineData("面板功能", "Panel")]
    [InlineData("动画", "Animation")]
    [InlineData("状态栏", "StatusProvider")]
    [InlineData("随便什么", null)]
    public void 无manifest时按父目录名推断kind(string parentGroup, string? expected)
    {
        var group = Dir(parentGroup);
        var child = Dir(parentGroup, "x");        // 不带 manifest

        Assert.Equal(expected, SeabedTreeScanner.FsKindOf(child));
        _ = group;
    }

    [Fact]
    public void manifest坏了_不抛异常也不影响其他行()
    {
        var panel = Dir("面板");
        var bad = Dir("面板", "坏清单");
        File_(bad, "manifest.json", "{ 这不是 JSON");
        Dir("面板", "正常的");

        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { panel };
        var nodes = Scan(expanded);

        Assert.Contains(nodes, n => n.Display == "坏清单");
        Assert.Contains(nodes, n => n.Display == "正常的");
    }

    // ==================== 筛选态 ====================

    [Fact]
    public void 筛选_全深度命中_并显示相对路径()
    {
        Dir("面板", "小组件", "calculator");
        Dir("面板", "小组件", "clipboard");
        Dir("动画", "尺寸形变");

        var nodes = Scan(filter: "calc");

        Assert.Single(nodes);
        Assert.Equal(Path.Combine("面板", "小组件", "calculator"), nodes[0].Display);
        Assert.Equal(0, nodes[0].Level);           // 筛选结果是扁平的
        Assert.True(nodes[0].FsIsDir);
    }

    [Fact]
    public void 筛选_目录与文件都能命中()
    {
        var d = Dir("面板", "小组件", "note");
        File_(d, "main.cs");
        File_(d, "note.xaml");

        var nodes = Scan(filter: "note");

        Assert.Contains(nodes, n => n.Display.EndsWith("note", StringComparison.Ordinal) && n.FsIsDir);
        Assert.Contains(nodes, n => n.Display.EndsWith("note.xaml", StringComparison.Ordinal) && !n.FsIsDir);
    }

    [Fact]
    public void 筛选_不命中就是空列表_不退回全量()
    {
        Dir("面板", "小组件", "calculator");
        Assert.Empty(Scan(filter: "绝对不存在的名字"));
    }

    [Fact]
    public void 筛选词为空白_等同于不筛选()
    {
        Dir("面板");
        Assert.False(SeabedTreeScanner.IsFilterActive("   "));
        Assert.Single(Scan(filter: "   "));       // 仍是整棵树
    }

    [Fact]
    public void 根目录不存在_返回空列表而不抛()
    {
        var nodes = SeabedTreeScanner.Scan(Path.Combine(_root, "不存在"), new HashSet<string>(), null);
        Assert.Empty(nodes);
    }

    // ==================== 内置（system）目录判定 ====================

    [Fact]
    public void 内置目录之下的路径_被识别为系统项()
    {
        // 删除前靠它给出"内置项"警告
        var panel = Dir("面板");
        Manifest(panel, "{ \"system\": true }");
        var deep = Dir("面板", "小组件", "calculator");

        Assert.True(SeabedTreeScanner.IsUnderSystemDir(deep, _root));
    }

    [Fact]
    public void 没有system标记_不算系统项_根目录本身也不算()
    {
        Dir("区域", "触发行为");

        Assert.False(SeabedTreeScanner.IsUnderSystemDir(Path.Combine(_root, "区域", "触发行为"), _root));
        Assert.False(SeabedTreeScanner.IsUnderSystemDir(_root, _root));   // 根本不参与判定
    }
}
