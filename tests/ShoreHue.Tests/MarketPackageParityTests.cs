using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 市场包（market/packages/&lt;id&gt;/）必须与内置源（seabed/小组件/&lt;id&gt;/）逐字一致。
///
/// 背景：这两份是**独立副本**，历史上改了一处忘了另一处 —— 结果"从市场装回来的小组件"
/// 长期停在旧实现（计时器面板隐藏即停表、计算器程序员模式失灵、划词翻译「打开设置」是死按钮、
/// 便签还是旧布局），而 LoadIssueVisibilityTests / MarketValidator 只验能否编译、能否构造，
/// **验不出行为差异** —— 这就是它烂了很久没人发现的原因。
/// 本测试把"改了内置件必须同步市场包"这条规则钉死。
/// </summary>
public class MarketPackageParityTests
{
    /// <summary>从测试程序集往上找仓库根（含 ShoreHue.csproj）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShoreHue.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根（ShoreHue.csproj）");
    }

    [Fact]
    public void 市场包的每个XAML形态文件_都必须与内置源逐字一致()
    {
        string root = RepoRoot();
        string market = Path.Combine(root, "market", "packages");
        string builtin = Path.Combine(root, "seabed", "小组件");
        Assert.True(Directory.Exists(market), "找不到 market/packages：" + market);
        Assert.True(Directory.Exists(builtin), "找不到 seabed/小组件：" + builtin);

        var compared = new List<string>();
        var problems = new List<string>();

        foreach (var pkgDir in Directory.GetDirectories(market))
        {
            string id = Path.GetFileName(pkgDir);
            string srcDir = Path.Combine(builtin, id);
            if (!Directory.Exists(srcDir)) continue;   // 该包没有对应的内置源（如纯代码包），跳过

            foreach (var srcFile in Directory.GetFiles(srcDir))
            {
                string name = Path.GetFileName(srcFile);
                bool isXaml = name.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
                if (!isXaml) continue;

                string dstFile = Path.Combine(pkgDir, name);
                if (!File.Exists(dstFile))
                {
                    problems.Add(id + "/" + name + "：市场包里缺这个文件（内置源有、市场包没有）");
                    continue;
                }
                if (!string.Equals(File.ReadAllText(srcFile), File.ReadAllText(dstFile), StringComparison.Ordinal))
                {
                    problems.Add(id + "/" + name + "：内容与内置源不一致");
                    continue;
                }
                compared.Add(id + "/" + name);
            }
        }

        // 防止"目录改名/清空后本测试静默变成空断言"
        if (compared.Count < 10)
            Assert.Fail("只比对了 " + compared.Count + " 个文件（应至少 10 个：5 个组件 × .xaml + .xaml.cs）。" +
                        "若确实删了市场包，请同步更新本测试的期望值。已比对：" + string.Join(", ", compared));

        if (problems.Count > 0)
            Assert.Fail("市场包与内置源不一致（改内置件时必须同步 market/packages）：\n" + string.Join("\n", problems));
    }
}
