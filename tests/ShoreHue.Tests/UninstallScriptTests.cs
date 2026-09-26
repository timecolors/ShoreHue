// ==================== 卸载脚本「真跑一遍」 ====================
//
// 前面 UninstallOwnedFilesTests 只钉住了「哪些文件算我的」这个判据本身；
// 这里把**生成的整段脚本交给 powershell.exe 真正执行**，钉住用户能看到的最终结果：
//
//   1) 有残留（目录里还有第三方 dll / 用户文件）→ 只删自己的文件、**保留目录**、桌面出现一份说明；
//   2) 干净（目录里只剩自己的文件，即单文件发布形态）→ 自己的文件删掉后目录为空 → **删掉目录、桌面不放任何东西**。
//
// 为什么要参数化 UninstallContext 才能测：脚本会动开始菜单快捷方式、注册表启动项、桌面。
// 参数化之后这里全部指向临时目录，且进程名用随机假名 —— 不会碰到正在运行的真实应用、
// 不会删你的快捷方式、不会往你桌面丢文件。（这正是"真跑一遍"和"只断言字符串"的区别。）
//
// 另钉一个沙盘实测出来的编码坑：脚本由 Windows PowerShell 5.1 执行，它的 Add-Content
// 默认按系统 ANSI（简中=GBK）写文件，日志里的中文在 UTF-8 环境下是乱码 ——
// 所以脚本改用 .NET 写无 BOM 的 UTF-8，这里用**严格 UTF-8 解码**把关。

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ShoreHue.Infrastructure.WinApi;
using Xunit;

namespace ShoreHue.Tests;

public class UninstallScriptTests
{
    private const string NoteSuffix = "-卸载残留说明.txt";

    private static string NewTempDir(string prefix = "sh-unin-test-")
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SafeDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    /// <summary>建一个"安装目录"：除了自己的文件，还塞进第三方 dll、用户文件、子目录。</summary>
    private static string NewInstallDirWithResidue()
    {
        string dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "ShoreHue.exe"), "");
        File.WriteAllText(Path.Combine(dir, "ShoreHue.dll"), "");
        File.WriteAllText(Path.Combine(dir, "ShoreHue.deps.json"), "");
        File.WriteAllText(Path.Combine(dir, "Microsoft.CodeAnalysis.dll"), "");  // 我们的依赖，但不认领
        File.WriteAllText(Path.Combine(dir, "NAudio.dll"), "");
        File.WriteAllText(Path.Combine(dir, "用户资料.txt"), "");                 // 用户自己的文件
        Directory.CreateDirectory(Path.Combine(dir, "某个子目录"));                // 目录一律不动
        return dir;
    }

    private static UninstallContext NewContext(
        string exeDir, string appName, bool deleteData, out string desktopDir, out string logFile)
    {
        desktopDir = NewTempDir("sh-unin-desktop-");
        logFile = Path.Combine(Path.GetTempPath(), "sh-unin-" + Guid.NewGuid().ToString("N") + ".log");
        return new UninstallContext
        {
            ExeDir = exeDir,
            DataDir = Path.Combine(Path.GetTempPath(), "sh-unin-data-" + Guid.NewGuid().ToString("N")),
            LnkPath = Path.Combine(Path.GetTempPath(), appName + ".lnk"),
            TempUpdateDir = Path.Combine(Path.GetTempPath(), "sh-unin-upd-" + Guid.NewGuid().ToString("N")),
            ScriptPath = Path.Combine(Path.GetTempPath(), "sh-unin-" + Guid.NewGuid().ToString("N") + ".ps1"),
            LogFile = logFile,
            DesktopDir = desktopDir,
            AppName = appName,
            DeleteData = deleteData
        };
    }

    /// <summary>像应用那样：把脚本写到磁盘，然后交给 powershell.exe 执行。</summary>
    private static void RunScript(UninstallContext ctx)
    {
        File.WriteAllText(ctx.ScriptPath, UninstallHelper.BuildScript(ctx), new UTF8Encoding(true));
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -File \"" + ctx.ScriptPath + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        Assert.NotNull(p);
        Assert.True(p!.WaitForExit(90_000), "卸载脚本 90 秒内没有结束");
    }

    // ---------- 分支 1：有残留 ----------

    [Fact]
    public void 有残留时只删自己的文件_保留目录_并在桌面留说明()
    {
        string exeDir = NewInstallDirWithResidue();
        string appName = "ShoreHueTest" + Guid.NewGuid().ToString("N")[..8];
        var ctx = NewContext(exeDir, appName, deleteData: true, out string desktopDir, out string logFile);
        // 数据目录 / 更新临时目录 / 快捷方式：都造出来，验证该删的确实删了
        Directory.CreateDirectory(ctx.DataDir);
        File.WriteAllText(Path.Combine(ctx.DataDir, "data.db"), "x");
        Directory.CreateDirectory(ctx.TempUpdateDir);
        File.WriteAllText(Path.Combine(ctx.TempUpdateDir, "new.zip"), "x");
        File.WriteAllText(ctx.LnkPath, "lnk");

        try
        {
            RunScript(ctx);

            // 自己的文件：删掉
            Assert.False(File.Exists(Path.Combine(exeDir, "ShoreHue.exe")));
            Assert.False(File.Exists(Path.Combine(exeDir, "ShoreHue.dll")));
            Assert.False(File.Exists(Path.Combine(exeDir, "ShoreHue.deps.json")));
            // 别人的东西：一个都不能少 —— 这是整条安全策略的重点
            Assert.True(File.Exists(Path.Combine(exeDir, "Microsoft.CodeAnalysis.dll")));
            Assert.True(File.Exists(Path.Combine(exeDir, "NAudio.dll")));
            Assert.True(File.Exists(Path.Combine(exeDir, "用户资料.txt")));
            Assert.True(Directory.Exists(Path.Combine(exeDir, "某个子目录")));
            // 目录本身必须还在（不能递归删 —— 那会删掉 Download 夹）
            Assert.True(Directory.Exists(exeDir));

            // 快捷方式删掉、数据目录按选择删掉、更新临时目录删掉
            Assert.False(File.Exists(ctx.LnkPath));
            Assert.False(Directory.Exists(ctx.DataDir));
            Assert.False(Directory.Exists(ctx.TempUpdateDir));

            // 桌面说明：出现，且写清了残留项数和安装目录
            string note = Path.Combine(desktopDir, appName + NoteSuffix);
            Assert.True(File.Exists(note), "有残留时应该在桌面留一份说明");
            string body = File.ReadAllText(note);
            Assert.Contains("还有 4 项", body);          // Microsoft dll + NAudio dll + 用户资料.txt + 某个子目录
            Assert.Contains(exeDir, body);

            // 日志：严格 UTF-8 可解（钉住 Add-Content 那个 GBK 坑），且无 BOM
            byte[] bytes = File.ReadAllBytes(logFile);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "日志不应该带 BOM");
            string log = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            Assert.Contains("安装目录里还有", log);

            // 脚本自删
            Assert.False(File.Exists(ctx.ScriptPath));
        }
        finally
        {
            SafeDelete(exeDir);
            SafeDelete(desktopDir);
            SafeDelete(ctx.DataDir);
            SafeDelete(ctx.TempUpdateDir);
            try { File.Delete(logFile); } catch { }
        }
    }

    // ---------- 分支 2：干净卸载 ----------

    [Fact]
    public void 干净卸载时删掉目录且桌面不放任何东西()
    {
        // 单文件发布形态：目录里只有自己的那一个 exe
        string exeDir = NewTempDir();
        File.WriteAllText(Path.Combine(exeDir, "ShoreHue.exe"), "");
        string appName = "ShoreHueTest" + Guid.NewGuid().ToString("N")[..8];
        var ctx = NewContext(exeDir, appName, deleteData: false, out string desktopDir, out _);

        try
        {
            RunScript(ctx);

            Assert.False(Directory.Exists(exeDir), "自己的文件删完后目录为空，就应该把目录也删掉");
            Assert.Empty(Directory.GetFileSystemEntries(desktopDir));   // 干净卸载不打扰用户
        }
        finally
        {
            SafeDelete(exeDir);
            SafeDelete(desktopDir);
        }
    }

    // ---------- 脚本本体的几个硬约束 ----------

    [Fact]
    public void 脚本不再使用AddContent_避免中文日志乱码()
    {
        string exeDir = NewTempDir();
        var ctx = NewContext(exeDir, "ShoreHueTestX", deleteData: false, out string desktopDir, out _);
        try
        {
            string script = UninstallHelper.BuildScript(ctx);
            Assert.DoesNotContain("Add-Content", script);
            Assert.Contains("System.Text.UTF8Encoding($false)", script);
        }
        finally { SafeDelete(exeDir); SafeDelete(desktopDir); }
    }

    [Fact]
    public void 单引号路径会被转义()
    {
        string exeDir = NewTempDir("sh-unin-it's-");
        var ctx = NewContext(exeDir, "ShoreHueTestX", deleteData: false, out string desktopDir, out _);
        try
        {
            string script = UninstallHelper.BuildScript(ctx);
            Assert.Contains("it''s", script);       // PowerShell 里单引号靠写两遍转义
        }
        finally { SafeDelete(exeDir); SafeDelete(desktopDir); }
    }

    [Fact]
    public void 不勾选删除数据时脚本里不出现数据目录()
    {
        string exeDir = NewTempDir();
        var ctx = NewContext(exeDir, "ShoreHueTestX", deleteData: false, out string desktopDir, out _);
        try
        {
            string script = UninstallHelper.BuildScript(ctx);
            Assert.DoesNotContain(ctx.DataDir, script);
            Assert.Contains(ctx.TempUpdateDir, script);      // 更新临时目录始终清理
        }
        finally { SafeDelete(exeDir); SafeDelete(desktopDir); }
    }

    [Fact]
    public void 只递归删数据目录_安装目录永远不递归()
    {
        string exeDir = NewTempDir();
        File.WriteAllText(Path.Combine(exeDir, "ShoreHue.exe"), "");
        var ctx = NewContext(exeDir, "ShoreHueTestX", deleteData: true, out string desktopDir, out _);
        try
        {
            string script = UninstallHelper.BuildScript(ctx);
            // -Recurse 只允许出现在第 4 段的 $dirs 循环里（数据目录/更新临时目录），全程仅此一处
            Assert.DoesNotContain("Remove-Item -LiteralPath $exeDir -Recurse", script);
            Assert.Equal(1, script.Split("-Recurse").Length - 1);
        }
        finally { SafeDelete(exeDir); SafeDelete(desktopDir); }
    }
}
