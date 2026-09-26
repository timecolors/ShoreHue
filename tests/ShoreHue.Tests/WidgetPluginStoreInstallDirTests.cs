using System;
using System.Collections.Generic;
using System.IO;
using ShoreHue.Core.Models;
using ShoreHue.Infrastructure.Utils;
using ShoreHue.UI.Seabed;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 市场安装的落盘位置（回归）：
/// 以前 main.cs/manifest.json 按配置树路径链落盘（面板/小组件/&lt;名字&gt;/），
/// 而 .xaml/.xaml.cs 由 WriteExtraFiles 另按「分类+显示名」落盘（面板/&lt;名字&gt;/）→ 同一个包被拆成两个目录。
/// 后果：XAML 那半截所在目录没有 manifest.json，加载端又要求「文件名 = 目录名」，
/// 于是要么被静默跳过（装包是残的），要么（文件名恰好等于目录名时）被当成
/// 「用户手写放进文件夹的代码」而免沙箱加载。
/// 另：附加文件不得改写 manifest.json —— 它是信任判定的输入。
/// </summary>
[Collection("WidgetStore")]   // 会改 AppPaths.TestDataRoot，必须与其它用例串行
public class WidgetPluginStoreInstallDirTests : IDisposable
{
    private readonly string _root;

    public WidgetPluginStoreInstallDirTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sh_installdir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        AppPaths.TestDataRoot = _root;
    }

    public void Dispose()
    {
        AppPaths.TestDataRoot = null;
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void 市场安装的附加文件_与主落盘同目录_且不得改写manifest()
    {
        var cp = new CustomPanelDefinition
        {
            Id = "probe-1", Name = "calculator", Category = "小组件",
            Kind = "Widget", BaseType = "Widget", ParentKey = "panel-widgets"
        };
        // ★ 关键前置：该目录已存在（历史 bug 正是"命中已存在的内置件目录"才写下岔的）
        Directory.CreateDirectory(Path.Combine(_root, "seabed", "面板", "calculator"));

        Assert.Equal("", WidgetPluginStore.SaveNodeToFolder(cp));
        Assert.Equal("", WidgetPluginStore.WriteExtraFiles(cp, new List<GitHubMarketService.PackageFile>
        {
            new("calculator.xaml", "<UserControl />"),
            new("calculator.xaml.cs", "// probe"),
            new("manifest.json", "{\"system\":true}"),   // 冒充内置件的信任判据
        }));

        string seabed = Path.Combine(_root, "seabed");
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(seabed, "*", SearchOption.AllDirectories))
            dirs.Add(Path.GetDirectoryName(f)!);
        if (dirs.Count != 1)
            Assert.Fail("同一个包的落盘文件必须都在同一个目录里，实际落在 " + dirs.Count + " 个目录：" + string.Join(" | ", dirs));

        string mf = Path.Combine(seabed, "面板", "小组件", "calculator", "manifest.json");
        Assert.True(File.Exists(mf), "宿主必须写出 manifest.json：" + mf);
        string content = File.ReadAllText(mf);
        if (!content.Contains("probe-1"))
            Assert.Fail("manifest.json 被附加文件覆盖了（应只由宿主写）。实际内容：" + content);
    }
}
