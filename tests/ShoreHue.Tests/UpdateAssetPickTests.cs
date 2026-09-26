// ==================== 自动更新「挑哪个资产」 ====================
//
// 钉住的是一次「顺手加个 zip 就会静默弄坏自动更新」的坑（2026-09-26 准备发 v1.1.1 时发现）：
//
// Release 里同时有 `ShoreHue.exe` 和 `ShoreHue-v1.1.1-win-x64.zip` 时，旧实现是
// 「遍历资产、取第一个名字以 .exe/.zip 结尾的」—— 而 `ShoreHue-` 按字母序排在 `ShoreHue.`
// 前面，于是大家都挑到 zip；可发布正文里只有**exe 的** SHA256（客户端是 fail-closed 比对），
// 下载 zip 后校验必然不匹配 → **所有用户的自动更新都被安全地拒绝**，
// 而且界面只会说「下载失败」，没人会想到根因是发布资产的顺序。
//
// 所以判据改成：**优先精确的 ShoreHue.exe**，其次才回退到任意 .exe/.zip
// （回退分支保留的意义：将来某版只发 zip、且正文写的是 zip 的哈希时仍然能用）。

using System;
using ShoreHue.Infrastructure.WinApi;
using Xunit;

namespace ShoreHue.Tests;

public class UpdateAssetPickTests
{
    /// <summary>本版真实的资产列表（GitHub 返回的顺序：按名字排序，zip 在前）。</summary>
    private static readonly string[] RealV111Assets =
    {
        "ShoreHue-v1.1.1-win-x64.zip",
        "ShoreHue.exe",
        "ShoreHue-1.1.1-x64.msix",
    };

    [Fact]
    public void 同时有zip和exe时挑exe_这就是那个坑()
    {
        Assert.Equal("ShoreHue.exe", UpdateService.PickUpdateAsset(RealV111Assets));
    }

    [Fact]
    public void 只有zip时回退到zip()
    {
        Assert.Equal("ShoreHue-v1.1.1-win-x64.zip",
            UpdateService.PickUpdateAsset(new[] { "ShoreHue-v1.1.1-win-x64.zip" }));
    }

    [Fact]
    public void 只有exe时挑exe()
    {
        Assert.Equal("ShoreHue.exe", UpdateService.PickUpdateAsset(new[] { "ShoreHue.exe" }));
    }

    [Fact]
    public void 只有msix时视为没有更新包()
    {
        // .msix 不能作为自动更新包（客户端只会解压 zip 或直接用 exe）
        Assert.Null(UpdateService.PickUpdateAsset(new[] { "ShoreHue-1.1.1-x64.msix" }));
    }

    [Fact]
    public void 大小写不敏感()
    {
        Assert.Equal("shorehue.exe", UpdateService.PickUpdateAsset(new[] { "a.zip", "shorehue.exe" }));
    }

    [Fact]
    public void 空列表与空名字都不会崩()
    {
        Assert.Null(UpdateService.PickUpdateAsset(Array.Empty<string>()));
        Assert.Null(UpdateService.PickUpdateAsset(new[] { "", null! }));
    }

    [Fact]
    public void 多个无关资产混入时不误挑()
    {
        Assert.Equal("ShoreHue.exe", UpdateService.PickUpdateAsset(new[]
        {
            "SHA256SUMS.txt", "ShoreHue-1.1.1-x64.msix", "Source code (zip)", "ShoreHue.exe",
        }));
    }

    [Fact]
    public void 排除zip回退时也不挑源码包()
    {
        // 「Source code (zip)」是 GitHub 自动生成的源码包，名字以 (zip) 结尾但不是 .zip 后缀
        Assert.Null(UpdateService.PickUpdateAsset(new[] { "Source code (zip)", "Source code (tar.gz)" }));
    }
}
