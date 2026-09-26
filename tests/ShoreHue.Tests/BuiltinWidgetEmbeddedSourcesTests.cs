// ==================== 内置小组件源码的「嵌入副本」 ====================
//
// 钉住的是一次**发布阻断**（2026-09 用官方 v1.1.0 exe 实测确认）：
// BuiltinTemplateSeeder 只从源码目录 seabed\小组件\<id>\ 读源码（靠 FindSourceRoot() 向上找
// ShoreHue.csproj）。用户机器上没有源码树 → srcRoot 为 null → 静默返回空 → 内置小组件
// 落不了盘，海床里只剩**空目录**（连 manifest.json 都没有）。
//
// 修复 = 在 ShoreHue.csproj 里对 seabed\小组件\** 加一条**通配**的 EmbeddedResource
// （资源名自动推导成 ShoreHue.Seabed.Widgets.<id>.<原文件名>），LoadWidgetFiles 在源码目录
// 不可用时回退读它。通配意味着**新增小组件不用改 csproj**，这个测试因此是纯粹的「发布形态
// 守卫」，而不是「提醒你去改构建文件」。
//
// ★ 为什么这个测试必须存在：开发机和 CI **都在有源码树的环境里跑**，走的是「源码目录」那条路，
//   嵌入资源就算整个坏掉也照样全绿 —— 唯一会暴露它的环境恰好是没人测的环境。

using System;
using System.IO;
using System.Linq;
using ShoreHue.UI.Seabed;
using ShoreHue.UI.Widgets.Dynamic;
using Xunit;

namespace ShoreHue.Tests;

public class BuiltinWidgetEmbeddedSourcesTests
{
    /// <summary>与 ShoreHue.csproj 里通配 EmbeddedResource 的 LogicalName 前缀一致。</summary>
    private const string Prefix = "ShoreHue.Seabed.Widgets.";

    [Theory]
    [InlineData("timer")]
    [InlineData("calculator")]
    [InlineData("clipboard")]
    [InlineData("note")]
    [InlineData("textai")]
    [InlineData("web")]
    public void 每个内置小组件都有可用的嵌入源码(string id)
    {
        string cs = ReadByKind(id, ".cs");
        Assert.False(string.IsNullOrWhiteSpace(cs),
            $"{id}：读不到嵌入的 .cs —— 发布版海床里会是空目录。csproj 的 EmbeddedResource 通配被删了？");
        Assert.Contains("class", cs);

        // web 是纯 C# 形态（没有 .xaml），其余 5 个是 XAML 形态
        if (id != "web")
        {
            string xaml = ReadByKind(id, ".xaml");
            Assert.False(string.IsNullOrWhiteSpace(xaml), $"{id}：读不到嵌入的 .xaml");
            Assert.Contains("xmlns", xaml);
        }
    }

    /// <summary>
    /// 嵌入资源必须覆盖「已迁移内置件」里的每一个小组件 —— 漏一个，发布版的海床里就少一个副本。
    /// 把「迁移清单」和「实际嵌进去的源码」绑在一起：以后往 MigratedBuiltinIds 加 id、
    /// 或者往 seabed\小组件\ 里加目录却没被通配覆盖到，这里会直接变红。
    /// </summary>
    [Fact]
    public void 嵌入资源覆盖全部已迁移的内置小组件()
    {
        var missing = WidgetPluginStore.MigratedBuiltinIds
            .Where(id => string.IsNullOrWhiteSpace(ReadByKind(id, ".cs")))
            .OrderBy(id => id)
            .ToList();

        Assert.True(missing.Count == 0,
            "以下已迁移的内置小组件读不到嵌入源码（发布版海床里会是空目录）：" + string.Join("、", missing));
    }

    /// <summary>
    /// 按 id + 形态读嵌入资源。资源名形如 <c>ShoreHue.Seabed.Widgets.&lt;id&gt;.&lt;原文件名&gt;</c>，
    /// 由 csproj 通配推导 —— 所以这里按「前缀 + 后缀」找，**不依赖具体文件名**（将来改名也不会假红）。
    /// </summary>
    private static string ReadByKind(string id, string kind)
    {
        var asm = typeof(BuiltinTemplateSeeder).Assembly;
        string prefix = Prefix + id + ".";
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            bool isXamlCs = name.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase);
            if (kind == ".xaml")
            {
                if (name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && !isXamlCs) return ReadRaw(asm, name);
            }
            else if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return ReadRaw(asm, name);
        }
        return "";
    }

    private static string ReadRaw(System.Reflection.Assembly asm, string name)
    {
        using var stream = asm.GetManifestResourceStream(name);
        if (stream == null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
