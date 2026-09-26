// ==================== 卸载时「哪些文件可以删」 ====================
//
// 钉住的是卸载安全策略的核心：**只删文件名以 `ShoreHue.` 开头的东西**。
//
// 背景（2026-09 隔离沙盘实测）：旧写法"目录里有 ShoreHue.dll 就整套递归删"，
// 等于赌"这个目录是我独占的" —— 而用户完全可能把整个 zip **摊在 Downloads / 桌面根目录**里，
// 那一下会删掉整个下载夹。
//
// 成熟安装器（Inno Setup 的 unins000.dat、MSI 的组件清单）靠**安装清单**做到既删干净又不误删，
// 前提是安装时写了清单；ShoreHue 没有安装步骤、拿不到清单，所以选**保守一侧**：
// 第三方依赖（Microsoft.* / NAudio.* / System.* 等）与用户文件**一律不动**。
// 误删用户文件的代价，远大于留几个残留 dll。

using System;
using System.IO;
using ShoreHue.Infrastructure.WinApi;
using Xunit;

namespace ShoreHue.Tests;

public class UninstallOwnedFilesTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sh-uninstall-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void 只认ShoreHue前缀_依赖与用户文件一律不动()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "ShoreHue.exe"), "");
            File.WriteAllText(Path.Combine(dir, "ShoreHue.dll"), "");
            File.WriteAllText(Path.Combine(dir, "ShoreHue.deps.json"), "");
            File.WriteAllText(Path.Combine(dir, "Microsoft.CodeAnalysis.dll"), "");   // 我们的依赖，但不归我们"认领"
            File.WriteAllText(Path.Combine(dir, "NAudio.dll"), "");
            File.WriteAllText(Path.Combine(dir, "用户资料.txt"), "");                  // 用户自己的文件
            Directory.CreateDirectory(Path.Combine(dir, "某个子目录"));                 // 目录一律不动

            var owned = UninstallHelper.OwnedFilesIn(dir);
            var names = owned.ConvertAll(Path.GetFileName);

            Assert.Equal(3, owned.Count);
            Assert.Contains("ShoreHue.exe", names);
            Assert.Contains("ShoreHue.dll", names);
            Assert.Contains("ShoreHue.deps.json", names);
            Assert.DoesNotContain("Microsoft.CodeAnalysis.dll", names);
            Assert.DoesNotContain("NAudio.dll", names);
            Assert.DoesNotContain("用户资料.txt", names);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 单文件形态下只认那一个exe()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "ShoreHue.exe"), "");
            File.WriteAllText(Path.Combine(dir, "用户资料.txt"), "");

            var owned = UninstallHelper.OwnedFilesIn(dir);

            Assert.Single(owned);
            Assert.Equal("ShoreHue.exe", Path.GetFileName(owned[0]));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void 目录不存在时返回空而不是抛异常()
    {
        string ghost = Path.Combine(Path.GetTempPath(), "根本不存在的目录-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(UninstallHelper.OwnedFilesIn(ghost));
    }

    [Fact]
    public void 大小写不敏感()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "SHOREHUE.EXE"), "");
            Assert.Single(UninstallHelper.OwnedFilesIn(dir));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
