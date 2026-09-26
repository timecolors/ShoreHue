// ==================== 嵌入的文档资源 ====================
//
// 钉住：AI 编程指南（docs/AI-PROGRAMMING.md）是**嵌入程序集**里的，而不是随 exe 摆放的外部文件。
//
// 为什么必须有这条：发布物是**单文件 exe**，CI 的 Release 只上传那一个文件。
// 曾经 docs/ 只靠 <None CopyToOutputDirectory> 复制成文件 —— 它进得了 bin 和 publish/，
// 却出不了 Release，于是发布版里点海床的「AI 编程指南」只能提示"文档未随程序发布"；
// 而开发机永远正常，所以这个失效长期没人发现（2026-09 实测确认）。
//
// ★ 与内置小组件源码同一个道理：开发机与 CI 都在有源码树的环境里跑，外部文件缺失照样全绿，
//   只有"嵌入资源"这条路在两种环境下都成立。

using System.IO;
using ShoreHue.UI.Settings.Pages;
using Xunit;

namespace ShoreHue.Tests;

public class EmbeddedDocsTests
{
    private const string ResourceName = "ShoreHue.Docs.AI-PROGRAMMING.md";

    [Fact]
    public void AI编程指南已嵌入程序集且内容非空()
    {
        var asm = typeof(SeabedPage).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName);
        Assert.False(stream is null,
            $"缺嵌入资源 {ResourceName} —— csproj 里的 EmbeddedResource（docs/AI-PROGRAMMING.md）被删了？"
            + "（发布版会退化成「文档未随程序发布」）");
        if (stream is null) return;

        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();

        Assert.False(string.IsNullOrWhiteSpace(text), "嵌入的 AI 编程指南内容为空");
        Assert.Contains("# ShoreHue AI 编程指南", text);
        // 文档里必须写明"面板下必须有 manifest"这条实际规则（曾经写的是扁平化前的 面板\小组件\ 结构）
        Assert.Contains("kind", text);
    }
}
