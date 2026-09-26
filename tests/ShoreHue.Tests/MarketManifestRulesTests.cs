using System.Linq;
using ShoreHue.UI.Seabed;
using Xunit;

namespace ShoreHue.Tests;

/// <summary>
/// 市场包的清单与文本规则（2026-09-13，借鉴 Windhawk 的流程纪律，见 docs\对比-Windhawk市场机制.md）。
///
/// 这批规则的价值在于把三类**静默失效**变成**提交时拦下**：
///   · 权限名写错 → 客户端不认识，能力被静默忽略（对应他们"校验你钩的 DLL 真的存在"）
///   · 隐形字符（零宽空格/双向控制符/NBSP）→ 人眼看不出来，但编译器与沙箱匹配会出怪事
///   · 清单 id 与实际路径不一致 → 客户端按路径安装，装到一个不存在的位置
/// </summary>
public class MarketManifestRulesTests
{
    private const string ValidManifest = """
    {
      "id": "timecolors/tide-note",
      "name": "潮池便签",
      "kind": "Widget",
      "version": "1.0.0",
      "author": "timecolors",
      "apiVersion": 1,
      "permissions": [ "clipboard" ],
      "files": [ "main.cs", "manifest.json" ]
    }
    """;

    private static MarketManifestRules.ManifestCheck Check(string? json, string id = "timecolors/tide-note")
        => MarketManifestRules.ValidateManifest(json, id);

    [Fact]
    public void 合法清单_通过()
    {
        var r = Check(ValidManifest);
        Assert.True(r.Ok, string.Join("；", r.Errors));
    }

    [Fact]
    public void 缺manifest_报错()
        => Assert.Contains("缺少 manifest.json", Check(null).Errors);

    [Fact]
    public void 清单不是JSON_报错()
        => Assert.Contains(Check("{ 这不是 JSON").Errors, e => e.Contains("不是合法 JSON"));

    [Fact]
    public void id与所在路径不一致_报错()
    {
        // 客户端按**路径**安装；两者不一致会装到不存在的位置
        var r = Check(ValidManifest, "timecolors/other-name");
        Assert.Contains(r.Errors, e => e.Contains("不一致"));
    }

    [Fact]
    public void 未知kind_报错()
        => Assert.Contains(Check(ValidManifest.Replace("\"kind\": \"Widget\"", "\"kind\": \"Gadget\"")).Errors,
                           e => e.Contains("kind"));

    [Fact]
    public void version格式不对_报错()
        => Assert.Contains(Check(ValidManifest.Replace("\"version\": \"1.0.0\"", "\"version\": \"v1\"")).Errors,
                           e => e.Contains("version"));

    [Fact]
    public void 权限名不存在于宿主白名单_报错()
    {
        // ★ 这条就是"引用的能力名必须真实存在"：AI 生成的包最容易写错权限名，
        //   而客户端不认识的权限名会被**静默忽略**（用户以为拿到了能力，其实没有）。
        var r = Check(ValidManifest.Replace("\"clipboard\"", "\"clipbord\""));
        Assert.Contains(r.Errors, e => e.Contains("clipbord") && e.Contains("不存在"));
    }

    [Fact]
    public void apiVersion高于客户端支持_报错()
        => Assert.Contains(Check(ValidManifest.Replace("\"apiVersion\": 1", "\"apiVersion\": 99")).Errors,
                           e => e.Contains("apiVersion"));

    [Fact]
    public void files里出现路径分隔符_报错()
        => Assert.Contains(Check(ValidManifest.Replace("\"main.cs\"", "\"../evil.cs\"")).Errors,
                           e => e.Contains("路径分隔符"));

    [Fact]
    public void 缺少files_报错()
        => Assert.Contains(Check(ValidManifest.Replace("\"files\": [ \"main.cs\", \"manifest.json\" ]", "\"files\": []")).Errors,
                           e => e.Contains("files"));

    [Fact]
    public void 未声明license_只提示不拦()
    {
        var r = Check(ValidManifest);
        Assert.True(r.Ok);
        Assert.Contains(r.Notes, n => n.Contains("MIT"));
    }

    [Fact]
    public void license写了但不合法_报错()
        => Assert.Contains(Check(ValidManifest.Replace("\"author\": \"timecolors\",", "\"author\": \"timecolors\", \"license\": \"随便写的\",")).Errors,
                           e => e.Contains("license"));

    // ==================== 文本：隐形字符与编码 ====================

    [Fact]
    public void 干净文本_无问题()
        => Assert.Empty(MarketManifestRules.ValidateText("main.cs", "// 中文注释没问题\nvar x = 1;\n"));

    [Fact]
    public void BOM开头_报错()
        => Assert.Contains(MarketManifestRules.ValidateText("main.cs", "\uFEFFvar x = 1;"),
                           e => e.Contains("BOM"));

    [Theory]
    [InlineData("\u200B", "零宽空格")]
    [InlineData("\u00A0", "NBSP")]
    [InlineData("\u202E", "双向控制符")]
    public void 隐形字符_报错并给出码点(string bad, string why)
    {
        var errors = MarketManifestRules.ValidateText("main.cs", "var x = 1;" + bad + "\n");
        Assert.Contains(errors, e => e.Contains("U+") && e.Contains(":1"));
        Assert.NotEmpty(errors);
        _ = why;
    }

    [Fact]
    public void 制表符与换行不算异常字符()
        => Assert.Empty(MarketManifestRules.ValidateText("main.cs", "a\tb\r\nc\n"));

    [Fact]
    public void 只检查包内的文本文件()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sh_mmr_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "main.cs"), "// a");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "w.xaml"), "<UserControl/>");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "manifest.json"), "{}");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "readme.md"), "不该被扫");

            var names = MarketManifestRules.TextFilesOf(dir).Select(System.IO.Path.GetFileName).ToList();
            Assert.Contains("main.cs", names);
            Assert.Contains("w.xaml", names);
            Assert.Contains("manifest.json", names);
            Assert.DoesNotContain("readme.md", names);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }
}
