using System;
using System.IO;
using System.Linq;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 区域元数据表（RegionTable）防漂移测试。
/// 这张表是 16 个区域「键 → 形状/尺寸/面板字段」的**唯一真相源**（此前散着四套字符串 switch + 三份键列表）。
/// 本测试盯三件事：① 键集合与顺序固定（加/减区域必须显式改测试）；② 委托真的写到预期字段；
/// ③ SettingsManager 的公开方法确实走表（读回一致）。
/// </summary>
[Collection("WidgetStore")]   // ★ 会改 AppPaths.TestDataRoot，必须串行（见 WidgetStoreCollection 注释）
public class RegionTableTests : IDisposable
{
    private readonly string _dir;

    public RegionTableTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sh_region_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AppPaths.TestDataRoot = _dir;
    }

    public void Dispose()
    {
        AppPaths.TestDataRoot = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>顺序 = 设置页区域列表顺序（改顺序会改设置页排布，所以要显式改这里）。</summary>
    private static readonly string[] ExpectedOrder =
    {
        "Top_Left", "Top_Center", "Top_Right",
        "Bottom_Left", "Bottom_Center", "Bottom_Right",
        "Left_Top", "Left_Center", "Left_Bottom",
        "Right_Top", "Right_Center", "Right_Bottom",
        "TopLeft", "TopRight", "BottomLeft", "BottomRight"
    };

    [Fact]
    public void 键集合与顺序_固定为16个()
    {
        Assert.Equal(ExpectedOrder, RegionTable.Keys);
        Assert.Equal(16, RegionTable.All.Count);
        Assert.Equal(4, RegionTable.All.Count(r => r.IsCorner));
        Assert.Equal(12, RegionTable.All.Count(r => !r.IsCorner));
    }

    [Fact]
    public void 边缘拆分键_与规范键互认()
    {
        Assert.Equal("Top_Left", RegionTable.KeyOf("Top", "Left"));
        Assert.Equal("Right_Bottom", RegionTable.KeyOf("Right", "Bottom"));
        Assert.Equal("", RegionTable.KeyOf("Bogus", "Left"));   // 拼不出已知键 → 空串（调用方按未知处理）
        Assert.Equal("", RegionTable.KeyOf("Top", ""));
    }

    [Fact]
    public void 别名与大小写_归一到规范键()
    {
        Assert.Equal("TopLeft", RegionTable.Normalize("Corner_TopLeft"));   // 别名（按字段名写法）
        Assert.Equal("Top_Left", RegionTable.Normalize("top_left"));        // 大小写不敏感
        Assert.Equal("nope", RegionTable.Normalize("nope"));                // 认不出来 → 原样返回
        Assert.Null(RegionTable.Find("nope"));
        Assert.Null(RegionTable.Find(""));
    }

    [Fact]
    public void 委托_写入预期字段_全16区域()
    {
        var d = new SettingsData();
        foreach (var r in RegionTable.All)
        {
            r.SetSize(d, 321, 123);
            r.SetPanel(d, "Widget");
            Assert.Equal((321d, 123d), r.GetSize(d));
            Assert.Equal("Widget", r.GetPanel(d));

            var copy = new SettingsData();
            Assert.Equal((0d, 0d), r.GetSize(copy));                          // 新数据默认未自定义
            Assert.Equal(RegionTable.DefaultPanel, r.GetPanel(copy));         // 新数据默认就是 "Default"
        }
    }

    [Fact]
    public void 四角_没有形状字段()
    {
        var d = new SettingsData();
        foreach (var r in RegionTable.All.Where(x => x.IsCorner))
        {
            Assert.Null(r.GetShape(d));      // 四角无形状字段 → SettingsManager 回落 DefaultShape
            r.SetShape(d, "Square");         // 不抛异常
            Assert.Null(r.GetShape(d));      // 也真的没地方可写（旧的 switch 同样是 default:return）
        }

        // 12 个边缘则有形状字段
        var edge = RegionTable.All.First(r => r.Key == "Top_Left");
        edge.SetShape(d, "Square");
        Assert.Equal("Square", edge.GetShape(d));
        Assert.Equal("Square", d.Region_Top_Left);
    }

    [Fact]
    public void 四角尺寸_写的是Corner字段()
    {
        var d = new SettingsData();
        RegionTable.Find("TopLeft")!.SetSize(d, 300, 200);
        Assert.Equal(300, d.UserWidth_Corner_TopLeft);   // ★ 四角多一层 Corner_（这正是当年容易漏改的地方）
        Assert.Equal(200, d.UserHeight_Corner_TopLeft);

        RegionTable.Find("Top_Left")!.SetSize(d, 111, 222);
        Assert.Equal(111, d.UserWidth_Top_Left);         // 边缘不带 Corner_
    }

    [Fact]
    public void SettingsManager_公开方法_走表且读写一致()
    {
        var mgr = new SettingsManager();
        var d = new SettingsData();
        mgr.Apply(d);

        foreach (var key in RegionTable.Keys)
        {
            mgr.SetRegionPanel(key, "Taskbar");
            mgr.SetUserSize(key, 260, 160);
            Assert.Equal("Taskbar", mgr.GetRegionPanel(key));
            Assert.Equal((260d, 160d), mgr.GetUserSize(key));
        }

        // 形状：边缘走 (edge, region) 老形态，四角恒为 Default
        mgr.SetRegionShape("Top", "Left", "Square");
        Assert.Equal("Square", mgr.GetRegionShape("Top", "Left"));
        Assert.Equal(RegionTable.DefaultShape, mgr.GetRegionShape("TopLeft", ""));

        // 未知键：读回落默认、写不抛（保持与原 switch 的 default 行为一致）
        Assert.Equal(RegionTable.DefaultPanel, mgr.GetRegionPanel("Bogus"));
        Assert.Equal((0d, 0d), mgr.GetUserSize("Bogus"));
        mgr.SetRegionPanel("Bogus", "Widget");
        mgr.SetUserSize("Bogus", 1, 1);
        Assert.Equal(RegionTable.DefaultPanel, mgr.GetRegionPanel("Bogus"));
    }

    /// <summary>
    /// ★ 重构前那四套 switch 的映射规则，**逐区域照抄成字面量表**（不是用公式重新推导）。
    ///
    /// 为什么必须是字面量：旧版这个测试用
    /// `corner ? "UserWidth_Corner_" + key : "UserWidth_" + key` 这样的**公式**算出期望字段名，
    /// 证明的只是"RegionTable 与测试自己刚发明的公式一致"——
    /// 一旦有人改了命名规则（或 RegionTable 的某一行写错了字段），两边会一起漂移，测试照样绿。
    /// 照抄成显式数据后，表里的每一个字段名都是**独立于被测实现**的记录：
    /// RegionTable 改错、或命名规则被改，这里就会红。
    ///
    /// 字段来源（见 docs/CODE-CLEANUP.md 第 4 批踩点笔记）：
    ///   · 形状：`Region_&lt;键&gt;` —— 只有 12 个边缘区域有；四角没有形状字段（旧 switch 的 default 分支）
    ///   · 尺寸：边缘 `UserWidth_&lt;键&gt;` / `UserHeight_&lt;键&gt;`；**四角多一层** `UserWidth_Corner_&lt;角名&gt;`
    ///   · 面板：`RegionPanel_&lt;键&gt;`（四角同样有）
    /// </summary>
    private sealed record LegacyRegionFields(
        string Key, bool IsCorner, string WidthField, string HeightField, string PanelField, string? ShapeField);

    private static readonly LegacyRegionFields[] LegacySwitches =
    {
        // ---- 12 个边缘区域：形状字段存在，尺寸字段不带 Corner_ ----
        new("Top_Left",      false, "UserWidth_Top_Left",      "UserHeight_Top_Left",      "RegionPanel_Top_Left",      "Region_Top_Left"),
        new("Top_Center",    false, "UserWidth_Top_Center",    "UserHeight_Top_Center",    "RegionPanel_Top_Center",    "Region_Top_Center"),
        new("Top_Right",     false, "UserWidth_Top_Right",     "UserHeight_Top_Right",     "RegionPanel_Top_Right",     "Region_Top_Right"),
        new("Bottom_Left",   false, "UserWidth_Bottom_Left",   "UserHeight_Bottom_Left",   "RegionPanel_Bottom_Left",   "Region_Bottom_Left"),
        new("Bottom_Center", false, "UserWidth_Bottom_Center", "UserHeight_Bottom_Center", "RegionPanel_Bottom_Center", "Region_Bottom_Center"),
        new("Bottom_Right",  false, "UserWidth_Bottom_Right",  "UserHeight_Bottom_Right",  "RegionPanel_Bottom_Right",  "Region_Bottom_Right"),
        new("Left_Top",      false, "UserWidth_Left_Top",      "UserHeight_Left_Top",      "RegionPanel_Left_Top",      "Region_Left_Top"),
        new("Left_Center",   false, "UserWidth_Left_Center",   "UserHeight_Left_Center",   "RegionPanel_Left_Center",   "Region_Left_Center"),
        new("Left_Bottom",   false, "UserWidth_Left_Bottom",   "UserHeight_Left_Bottom",   "RegionPanel_Left_Bottom",   "Region_Left_Bottom"),
        new("Right_Top",     false, "UserWidth_Right_Top",     "UserHeight_Right_Top",     "RegionPanel_Right_Top",     "Region_Right_Top"),
        new("Right_Center",  false, "UserWidth_Right_Center",  "UserHeight_Right_Center",  "RegionPanel_Right_Center",  "Region_Right_Center"),
        new("Right_Bottom",  false, "UserWidth_Right_Bottom",  "UserHeight_Right_Bottom",  "RegionPanel_Right_Bottom",  "Region_Right_Bottom"),
        // ---- 4 个角：无形状字段；尺寸字段多一层 Corner_ ----
        new("TopLeft",     true, "UserWidth_Corner_TopLeft",     "UserHeight_Corner_TopLeft",     "RegionPanel_TopLeft",     null),
        new("TopRight",    true, "UserWidth_Corner_TopRight",    "UserHeight_Corner_TopRight",    "RegionPanel_TopRight",    null),
        new("BottomLeft",  true, "UserWidth_Corner_BottomLeft",  "UserHeight_Corner_BottomLeft",  "RegionPanel_BottomLeft",  null),
        new("BottomRight", true, "UserWidth_Corner_BottomRight", "UserHeight_Corner_BottomRight", "RegionPanel_BottomRight", null),
    };

    /// <summary>
    /// ★ 等价性回归：以**照抄旧 switch 的字面量表**为参照，逐区域比对重构后
    /// （RegionTable + SettingsManager）实际写进去的字段。
    /// 表键集合还必须与 RegionTable.Keys 完全一致 —— 新增/删除区域时必须同时改这里。
    /// </summary>
    [Fact]
    public void 与原四套switch_逐区域等价()
    {
        var d = new SettingsData();
        var mgr = new SettingsManager();
        mgr.Apply(d);   // Apply 后 SettingsManager 内部持有的就是 d 这个实例，可直接反射核对字段

        Assert.Equal(LegacySwitches.Select(r => r.Key), RegionTable.Keys);

        foreach (var row in LegacySwitches)
        {
            // 参照表自身的自洽性：字面量必须与 RegionTable 的 IsCorner 判定一致
            Assert.Equal(row.IsCorner, RegionTable.Find(row.Key)!.IsCorner);

            // 尺寸
            mgr.SetUserSize(row.Key, 7, 9);
            Assert.Equal(7d, (double)typeof(SettingsData).GetProperty(row.WidthField)!.GetValue(d)!);
            Assert.Equal(9d, (double)typeof(SettingsData).GetProperty(row.HeightField)!.GetValue(d)!);

            // 面板
            mgr.SetRegionPanel(row.Key, "Widget");
            Assert.Equal("Widget", typeof(SettingsData).GetProperty(row.PanelField)!.GetValue(d));

            // 形状：边缘写 Region_<键>；四角没有该字段（旧 switch 的 default 分支 = 不落盘）
            if (row.ShapeField != null)
            {
                var parts = row.Key.Split('_');
                mgr.SetRegionShape(parts[0], parts[1], "Square");
                Assert.Equal("Square", typeof(SettingsData).GetProperty(row.ShapeField)!.GetValue(d));
            }
            else
            {
                // 四角键没有下划线：旧调用形态就是"拼不出 edge_region"，表里也不该有形状字段
                Assert.DoesNotContain("Region_" + row.Key,
                    typeof(SettingsData).GetProperties().Select(p => p.Name));
                mgr.SetRegionShape(row.Key, "", "Square");   // 不该抛、也不该写到别处
            }
        }
    }

    [Fact]
    public void 别名也认_走表时等价于规范键()
    {
        var mgr = new SettingsManager();
        mgr.Apply(new SettingsData());
        mgr.SetRegionPanel("Corner_TopLeft", "AI");          // 别名写法
        Assert.Equal("AI", mgr.GetRegionPanel("TopLeft"));    // 规范键读得到同一个字段
    }
}
