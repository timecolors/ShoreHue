using System;
using System.IO;
using System.Linq;
using ShoreHue.UI.Seabed;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 海床文件操作与新建模板（2026-09-13）。
///
/// 这批逻辑原本塞在 `SeabedPage`（当时已 2500+ 行）里，**根本没法测** —— 要测就得先造一个 WPF 页面。
/// 拆成 `SeabedFs`（纯 IO）与 `SeabedFileTemplates`（纯字符串）之后，这里可以无头、确定性地验证，
/// 包括几条**用户看不见但会咬人**的性质：
///   · 新建出来的骨架必须真能用（否则用户建完看到一堆报错，不如不建）
///   · `.xaml` 必须连 `.xaml.cs` 一起建（单独一个 .xaml 加载不了）
///   · 拖入同名文件**绝不覆盖**（宁可不做，也不能静默改掉用户已有的文件）
///   · 目录重命名必须同步 manifest.name（否则树↔文件夹两份真相不一致）
/// </summary>
public class SeabedFsTests : IDisposable
{
    private readonly string _root;

    public SeabedFsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sh_seabedfs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    private string NewDir(string name)
    {
        string p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    // ==================== 模板 ====================

    [Fact]
    public void 纯代码骨架_具备IWidget要求的全部成员()
    {
        string cs = SeabedFileTemplates.CsWidget("main");

        Assert.Contains("ShoreHue.UI.Widgets.IWidget", cs);
        Assert.Contains("public string Name", cs);
        Assert.Contains("public UserControl CreateView()", cs);
        Assert.Contains("public void OnActivated()", cs);
        Assert.Contains("public void OnDeactivated()", cs);
        Assert.Contains("class MyWidget", cs);       // main 不是类名 → 给中性默认
    }

    [Fact]
    public void XAML骨架_事件所在元素必须带xName()
    {
        // ★ 这条是今天踩过的坑：受限方言是「XAML → C# 代码生成」，事件只能挂到生成出来的具名字段上，
        //   宿主元素没有 x:Name 时事件会被**静默丢弃**（编译零错、按钮点了没反应）。
        //   模板自己就必须示范正确写法，否则用户照抄就中招。
        string xaml = SeabedFileTemplates.Xaml();

        Assert.Contains("Click=\"BtnHello_Click\"", xaml);
        Assert.Contains("x:Name=\"BtnHello\"", xaml);
    }

    [Fact]
    public void 完全编程骨架_类名与文件名一致且实现IWidget()
    {
        string cs = SeabedFileTemplates.XamlCs("我的功能");
        Assert.Contains("public partial class 我的功能", cs);
        Assert.Contains("InitializeComponent()", cs);
        Assert.Contains("ShoreHue.UI.Widgets.IWidget", cs);
    }

    [Theory]
    [InlineData("main", "MyWidget")]
    [InlineData("", "MyWidget")]
    [InlineData("7zip", "W7zip")]          // 数字开头 → 补前缀，否则不是合法类名
    [InlineData("my-widget", "mywidget")]  // 非法字符剔除
    [InlineData("MyWidget", "MyWidget")]
    public void 类名清洗(string stem, string expected)
        => Assert.Equal(expected, SeabedFileTemplates.SafeClassName(stem));

    // ==================== 新建 ====================

    [Fact]
    public void 新建文件_写出模板且不覆盖同名()
    {
        string dir = NewDir("我的小组件");
        var r = SeabedFs.CreateFile(dir, "main.cs");

        Assert.True(r.Ok, r.Message);
        string p = Path.Combine(dir, "main.cs");
        Assert.True(File.Exists(p));
        Assert.Contains("IWidget", File.ReadAllText(p));

        // 同名再来一次：拒绝，且**原文件内容不被改动**
        string before = File.ReadAllText(p);
        var again = SeabedFs.CreateFile(dir, "main.cs");
        Assert.False(again.Ok);
        Assert.Contains("同名已存在", again.Message);
        Assert.Equal(before, File.ReadAllText(p));
    }

    [Fact]
    public void 新建XAML_会连代码后置一起建_并对文件名与目录名不一致给出警告()
    {
        string dir = NewDir("我的面板");
        var r = SeabedFs.CreateFile(dir, "别的名字.xaml");

        Assert.True(r.Ok, r.Message);
        Assert.True(File.Exists(Path.Combine(dir, "别的名字.xaml")));
        Assert.True(File.Exists(Path.Combine(dir, "别的名字.xaml.cs")));   // 单独一个 .xaml 加载不了
        Assert.NotNull(r.Warn);                                            // 文件名≠目录名 → 必须提醒
        Assert.Contains("我的面板", r.Warn);
    }

    [Fact]
    public void 新建XAML_文件名与目录名一致时不警告()
    {
        string dir = NewDir("panel-x");
        var r = SeabedFs.CreateFile(dir, "panel-x.xaml");
        Assert.True(r.Ok, r.Message);
        Assert.Null(r.Warn);
    }

    [Fact]
    public void 新建文件夹与名字校验()
    {
        string dir = NewDir("父目录");
        Assert.True(SeabedFs.CreateFolder(dir, "子目录").Ok);
        Assert.True(Directory.Exists(Path.Combine(dir, "子目录")));

        Assert.False(SeabedFs.CreateFolder(dir, "子目录").Ok);      // 同名
        Assert.False(SeabedFs.CreateFolder(dir, "").Ok);            // 空
        Assert.False(SeabedFs.CreateFolder(dir, "a/b").Ok);         // 含非法字符
    }

    // ==================== 重命名 ====================

    [Fact]
    public void 目录重命名_同步manifest里的name()
    {
        string dir = NewDir("旧名");
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{ \"id\": \"fixed-id\", \"name\": \"旧名\", \"kind\": \"Widget\" }");

        var r = SeabedFs.Rename(dir, isDir: true, "新名");

        Assert.True(r.Ok, r.Message);
        Assert.False(Directory.Exists(dir));
        string moved = Path.Combine(_root, "新名");
        Assert.True(Directory.Exists(moved));
        // ★ 必须按**语义**比对，不能比字面量：JsonSerializer 默认把非 ASCII 转义成 \uXXXX
        //   （项目既有的 manifest 就是这样写的，例如计算器清单里的 "\u8BA1\u7B97\u5668"）。
        string json = File.ReadAllText(Path.Combine(moved, "manifest.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("新名", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("fixed-id", doc.RootElement.GetProperty("id").GetString());   // ★ id 必须保持稳定
    }

    [Fact]
    public void 文件重命名_不动manifest且拒绝同名()
    {
        string dir = NewDir("d1");
        File.WriteAllText(Path.Combine(dir, "a.cs"), "// a");
        File.WriteAllText(Path.Combine(dir, "b.cs"), "// b");

        Assert.False(SeabedFs.Rename(Path.Combine(dir, "a.cs"), isDir: false, "b.cs").Ok);   // 同名拒绝
        var r = SeabedFs.Rename(Path.Combine(dir, "a.cs"), isDir: false, "c.cs");
        Assert.True(r.Ok, r.Message);
        Assert.True(File.Exists(Path.Combine(dir, "c.cs")));
        Assert.False(File.Exists(Path.Combine(dir, "a.cs")));
    }

    // ==================== 拖入 ====================

    [Fact]
    public void 拖入_复制文件与目录_同名跳过且不覆盖()
    {
        string dest = NewDir("dest");
        string srcDir = NewDir("src");
        File.WriteAllText(Path.Combine(srcDir, "one.cs"), "// one");
        string sub = Path.Combine(srcDir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "inner.cs"), "// inner");

        var r1 = SeabedFs.CopyInto(new[] { Path.Combine(srcDir, "one.cs"), sub }, dest);
        Assert.True(r1.Ok, r1.Message);
        Assert.Equal("// one", File.ReadAllText(Path.Combine(dest, "one.cs")));
        Assert.True(File.Exists(Path.Combine(dest, "sub", "inner.cs")));

        // ★ 同名再来：跳过、**不覆盖**，而且源文件不受影响
        File.WriteAllText(Path.Combine(dest, "one.cs"), "// 用户改过的");
        var r2 = SeabedFs.CopyInto(new[] { Path.Combine(srcDir, "one.cs") }, dest);
        Assert.Contains("跳过同名", r2.Message);
        Assert.Equal("// 用户改过的", File.ReadAllText(Path.Combine(dest, "one.cs")));
        Assert.Equal("// one", File.ReadAllText(Path.Combine(srcDir, "one.cs")));   // 源文件没被动
    }

    [Fact]
    public void 拖入到不存在的目录_明确失败()
    {
        var r = SeabedFs.CopyInto(new[] { "whatever" }, Path.Combine(_root, "不存在"));
        Assert.False(r.Ok);
        Assert.Contains("目标目录不存在", r.Message);
    }

    // ==================== 删除（回收站） ====================

    [Fact]
    public void 拒绝删除海床根目录()
    {
        // ★ 硬约束：以前它只是页面里一句 if，谁都测不到；现在抽到 SeabedFs 就能钉住。
        var r = SeabedFs.DeleteToRecycleBin(_root, isDir: true, _root);

        Assert.False(r.Ok);
        Assert.Contains("不能删除海床根目录", r.Message);
        Assert.True(Directory.Exists(_root));                 // 根目录必须还在
    }

    [Fact]
    public void 删除不存在的路径_明确失败而不是静默成功()
    {
        var r = SeabedFs.DeleteToRecycleBin(Path.Combine(_root, "不存在"), isDir: false, _root);
        Assert.False(r.Ok);
        Assert.Contains("找不到", r.Message);
    }

    [Fact]
    public void 删除文件与目录_都真的删掉()
    {
        // 走回收站（可恢复）；若该 API 在测试环境不可用则回退永久删除 —— 两条路都必须让文件消失
        string file = Path.Combine(_root, "a.cs");
        File.WriteAllText(file, "// a");
        string sub = NewDir("sub");

        Assert.True(SeabedFs.DeleteToRecycleBin(file, isDir: false, _root).Ok);
        Assert.False(File.Exists(file));

        Assert.True(SeabedFs.DeleteToRecycleBin(sub, isDir: true, _root).Ok);
        Assert.False(Directory.Exists(sub));
    }

    // ==================== 保存前的「外部改动」判定 ====================

    [Fact]
    public void 改过且磁盘也被人改过_必须提示覆盖()
    {
        // 核心情形：我改了（editing ≠ snapshot），磁盘又是第三个版本 → 覆盖会丢别人的改动
        Assert.True(SeabedFs.ShouldWarnBeforeOverwrite("原始", "别人改的", "我改的"));
    }

    [Fact]
    public void 改过但磁盘没人动过_不提示()
        => Assert.False(SeabedFs.ShouldWarnBeforeOverwrite("原始", "原始", "我改的"));

    [Fact]
    public void 磁盘内容恰好等于我要写的_不提示()
        => Assert.False(SeabedFs.ShouldWarnBeforeOverwrite("原始", "我改的", "我改的"));

    [Fact]
    public void 我自己没改过_不是提示覆盖而是重新载入()
    {
        // ★ 这个组合以前是**裸奔**的：我没编辑 → ShouldWarnBeforeOverwrite 放行 → 把打开时的旧内容写回去，
        //   别人的改动无声消失（watcher 漏事件时就会走到这里）。
        //   现在归 ShouldReloadFromDisk 管：不写盘、改成载入磁盘上的新版本（VS Code 对干净缓冲区的做法）。
        const string snap = "原始", disk = "别人改的";
        Assert.False(SeabedFs.ShouldWarnBeforeOverwrite(snap, disk, snap));   // 不弹框
        Assert.True(SeabedFs.ShouldReloadFromDisk(snap, snap, disk));         // 改判成重载
    }

    [Fact]
    public void 重新载入_只在没改过且磁盘变了时成立()
    {
        Assert.False(SeabedFs.ShouldReloadFromDisk("原始", "原始", "原始"));   // 磁盘没变 → 正常写盘
        Assert.False(SeabedFs.ShouldReloadFromDisk("原始", "我改的", "别人改的")); // 我改过 → 走"提示覆盖"那条路
        Assert.False(SeabedFs.ShouldReloadFromDisk("原始", "我改的", "原始"));   // 只有我改过 → 正常保存
    }

    [Fact]
    public void 重新载入与提示覆盖_互斥且覆盖全部情形()
    {
        string[] vals = { "原始", "我改的", "别人改的" };
        foreach (var snap in vals)
            foreach (var editing in vals)
                foreach (var disk in vals)
                    Assert.False(SeabedFs.ShouldWarnBeforeOverwrite(snap, disk, editing)
                                 && SeabedFs.ShouldReloadFromDisk(snap, editing, disk));
    }
}
