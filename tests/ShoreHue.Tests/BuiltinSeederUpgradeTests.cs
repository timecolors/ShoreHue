using System;
using System.Collections.Generic;
using System.IO;
using ShoreHue.UI.Seabed;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 内置模板「落盘 / 升级」规则（BuiltinTemplateSeeder.SyncTemplateFile）。
///
/// 背景：老实现是"目标文件已存在就永不覆盖"，于是运行时副本**永久冻结**在首次落盘的版本上 ——
/// 实测咬过一次：给内置件补 IWidgetFooter 后，从文件夹加载的计时器页脚直接消失（编译能过、日志全绿）。
/// 新规则：manifest 里记落盘哈希，磁盘哈希 == 落盘哈希（用户没动过）才随源码升级；对不上就一个字节都不碰。
/// </summary>
public class BuiltinSeederUpgradeTests : IDisposable
{
    private readonly string _dir;

    public BuiltinSeederUpgradeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sh_seed_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    [Fact]
    public void 目标不存在时落盘并记下基线()
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(BuiltinTemplateSeeder.SyncTemplateFile(_dir, "main.cs", "// v1", hashes));

        Assert.Equal("// v1", File.ReadAllText(PathOf("main.cs")));
        Assert.True(hashes.ContainsKey("main.cs"));
    }

    [Fact]
    public void 用户没改过时随源码升级()
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        BuiltinTemplateSeeder.SyncTemplateFile(_dir, "main.cs", "// v1", hashes);

        // 源码变了，磁盘还是上次落的 v1 → 应当升级
        Assert.True(BuiltinTemplateSeeder.SyncTemplateFile(_dir, "main.cs", "// v2", hashes));

        Assert.Equal("// v2", File.ReadAllText(PathOf("main.cs")));
        // 基线要跟着刷新，否则下一次会把 v2 当成"用户改的"
        Assert.True(BuiltinTemplateSeeder.SyncTemplateFile(_dir, "main.cs", "// v3", hashes));
        Assert.Equal("// v3", File.ReadAllText(PathOf("main.cs")));
    }

    [Fact]
    public void 用户改过时一个字节都不碰()
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        BuiltinTemplateSeeder.SyncTemplateFile(_dir, "main.cs", "// v1", hashes);
        File.WriteAllText(PathOf("main.cs"), "// 用户自己改的");

        Assert.False(BuiltinTemplateSeeder.SyncTemplateFile(_dir, "main.cs", "// v2", hashes));

        Assert.Equal("// 用户自己改的", File.ReadAllText(PathOf("main.cs")));
    }

    [Fact]
    public void 没有基线记录时只登记不覆盖_下一次才允许升级()
    {
        // 模拟老版本 manifest：文件在、但没有任何 seededFiles 记录
        File.WriteAllText(PathOf("main.cs"), "// 老版本留下的");
        var hashes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Assert.False(BuiltinTemplateSeeder.SyncTemplateFile(_dir, "main.cs", "// 新源码", hashes));
        Assert.Equal("// 老版本留下的", File.ReadAllText(PathOf("main.cs")));
        Assert.True(hashes.ContainsKey("main.cs"));

        // 建立基线之后，就按正常规则走（用户没动过 → 可以升级）
        Assert.True(BuiltinTemplateSeeder.SyncTemplateFile(_dir, "main.cs", "// 新源码", hashes));
        Assert.Equal("// 新源码", File.ReadAllText(PathOf("main.cs")));
    }

    [Fact]
    public void 源码没变时不写盘()
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        BuiltinTemplateSeeder.SyncTemplateFile(_dir, "main.cs", "// v1", hashes);
        var before = File.GetLastWriteTimeUtc(PathOf("main.cs"));

        Assert.False(BuiltinTemplateSeeder.SyncTemplateFile(_dir, "main.cs", "// v1", hashes));

        Assert.Equal(before, File.GetLastWriteTimeUtc(PathOf("main.cs")));
    }
}
