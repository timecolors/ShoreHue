// ==================== 便签数据层：写盘安全与通知 ====================
//
// 盯的是三件"以前会真丢用户数据"的事：
//   ① 每次按键整份重写 → 现在去抖；② 非原子写 + 无备份 → 现在 .tmp + File.Replace + .bak；
//   ③ 解析失败被当成"没有便签"，下一次自动保存就把坏文件覆盖成空 → 现在留档改名且不覆盖。
// 数据目录用 AppPaths.TestDataRoot 隔离，绝不碰用户真实的 notes.json。

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using Xunit;

namespace ShoreHue.Tests;

public class NoteManagerTests : IDisposable
{
    private readonly string _dir;

    public NoteManagerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sh_notes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AppPaths.TestDataRoot = _dir;
    }

    public void Dispose()
    {
        AppPaths.TestDataRoot = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>
    /// ★ 数据文件路径**显式传入**，不依赖 AppPaths.TestDataRoot：
    ///   那是个静态开关，而 xUnit 并行跑测试类 → 别的测试清理它时会把这里的路径带走
    ///   （真机上表现为"文件忽然不存在"的偶发假红，刚踩过）。
    /// </summary>
    private NoteManager NewManager() => new(new SettingsManager(), Path.Combine(_dir, "notes.json"));
    private string NotesFile => Path.Combine(_dir, "notes.json");

    [Fact]
    public void 保存后能读回_且内容走原子写路径()
    {
        var m = NewManager();
        m.Initialize();
        var n = m.CreateNote("便签 1");
        m.UpdateNoteContent(n, "内容甲");
        m.Save();

        Assert.True(File.Exists(NotesFile));
        Assert.False(File.Exists(NotesFile + ".tmp"), "原子写的临时文件必须被搬走，不能留在目录里");

        var m2 = NewManager();
        m2.Initialize();
        Assert.Single(m2.Notes);
        Assert.Equal("便签 1", m2.Notes[0].Title);
        Assert.Equal("内容甲", m2.Notes[0].Content);
    }

    [Fact]
    public void 第二次保存会留下_bak_备份()
    {
        var m = NewManager();
        m.Initialize();
        var n = m.CreateNote("A");
        m.UpdateNoteContent(n, "v1");
        m.Save();
        m.UpdateNoteContent(n, "v2");
        m.Save();

        Assert.True(File.Exists(NotesFile + ".bak"), "File.Replace 应把上一版挪到 .bak");
        string bak = File.ReadAllText(NotesFile + ".bak");
        Assert.Contains("v1", bak);
        Assert.Contains("v2", File.ReadAllText(NotesFile));
    }

    [Fact]
    public void 数据文件损坏_留档改名且绝不被后续保存覆盖()
    {
        File.WriteAllText(NotesFile, "{ 这不是合法 JSON ");

        var m = NewManager();
        m.Initialize();

        Assert.Empty(m.Notes);
        Assert.False(string.IsNullOrEmpty(m.LoadWarning));                       // 必须能告诉用户
        var kept = Directory.GetFiles(_dir, "notes.json.corrupt-*");
        Assert.Single(kept);                                                     // 坏文件被留档
        Assert.Contains("这不是合法 JSON", File.ReadAllText(kept[0]));            // 原始字节仍在，可人工抢救

        // 之后哪怕用户继续用（新建 + 保存），留档也不该被抹掉
        m.CreateNote("新便签");
        m.Save();
        Assert.Single(Directory.GetFiles(_dir, "notes.json.corrupt-*"));
        Assert.Single(m.Notes);
    }

    [Fact]
    public void 正文输入走去抖_不每个键都写盘()
    {
        var m = NewManager();
        m.Initialize();
        var n = m.CreateNote("N");
        m.Save();
        string before = File.ReadAllText(NotesFile);

        m.UpdateNoteContent(n, "v-typed");
        Assert.Equal(before, File.ReadAllText(NotesFile));   // 去抖窗口内：磁盘还没变

        m.Flush();                                           // 切走/退出时立刻落
        Assert.Contains("v-typed", File.ReadAllText(NotesFile));
    }

    [Fact]
    public void 去抖到点会自动落盘()
    {
        var m = NewManager();
        m.Initialize();
        var n = m.CreateNote("N");
        m.Save();

        m.UpdateNoteContent(n, "v-later");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !File.ReadAllText(NotesFile).Contains("v-later"))
            System.Threading.Thread.Sleep(100);

        Assert.Contains("v-later", File.ReadAllText(NotesFile));
    }

    [Fact]
    public void 中文内容按语义读回_文件里是转义过的所以不能按字面找()
    {
        // ★ JsonSerializer 默认把非 ASCII 转义成 \uXXXX（manifest.json 也是这个样子）——
        //   断言必须走反序列化，按字面 Contains 找中文必然失败（本测试替这一坑留个记录）。
        var m = NewManager();
        m.Initialize();
        var n = m.CreateNote("便签甲");
        m.UpdateNoteContent(n, "内容是中文");
        m.Save();

        Assert.DoesNotContain("内容是中文", File.ReadAllText(NotesFile));
        var back = JsonSerializer.Deserialize<System.Collections.ObjectModel.ObservableCollection<NoteItem>>(File.ReadAllText(NotesFile))!;
        Assert.Equal("便签甲", back[0].Title);
        Assert.Equal("内容是中文", back[0].Content);
    }

    [Fact]
    public void 重新载入时恢复上次选中的那条便签()
    {
        var m = NewManager();
        m.Initialize();
        var a = m.CreateNote("A");
        var b = m.CreateNote("B");
        m.SetCurrentNote(a);
        foreach (var x in m.Notes) x.IsCurrent = x == a;
        m.Save();

        var m2 = NewManager();
        m2.Initialize();
        Assert.NotNull(m2.CurrentNote);
        Assert.Equal("A", m2.CurrentNote!.Title);
    }

    [Fact]
    public void 便签项会发属性通知_否则标签名与高亮不会刷新()
    {
        var n = new NoteItem { Title = "旧" };
        var changed = new System.Collections.Generic.List<string>();
        n.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        n.Title = "新";
        n.IsCurrent = true;
        n.Color = "#FF8000";

        Assert.Contains("Title", changed);
        Assert.Contains("IsCurrent", changed);
        Assert.Contains("Color", changed);
    }
}
