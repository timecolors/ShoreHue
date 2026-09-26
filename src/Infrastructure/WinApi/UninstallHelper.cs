using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Infrastructure.Utils;

namespace ShoreHue.Infrastructure.WinApi
{
    /// <summary>
    /// 生成卸载脚本所需的一切输入。
    ///
    /// 抽成参数不是为了"可配置"，而是为了**可实测**：脚本会动开始菜单快捷方式、注册表启动项、
    /// 桌面 —— 这些路径写死在函数体里，就只能在真实环境上做实验（真的删掉你的快捷方式、
    /// 真的往你桌面丢文件）。参数化之后，测试可以在临时目录里把整条卸载流程真跑一遍。
    /// </summary>
    internal sealed class UninstallContext
    {
        /// <summary>安装目录。脚本只会删这里面**名字以 `ShoreHue.` 开头**的文件，从不递归删目录。</summary>
        public required string ExeDir { get; init; }

        /// <summary>本地数据目录 %LOCALAPPDATA%\ShoreHue。</summary>
        public required string DataDir { get; init; }

        /// <summary>开始菜单快捷方式路径。</summary>
        public required string LnkPath { get; init; }

        /// <summary>更新临时目录。</summary>
        public required string TempUpdateDir { get; init; }

        /// <summary>脚本自身路径（执行完自删）。</summary>
        public required string ScriptPath { get; init; }

        /// <summary>日志文件路径。</summary>
        public required string LogFile { get; init; }

        /// <summary>桌面目录：有残留时在这里放一份说明；空字符串 = 不写。</summary>
        public required string DesktopDir { get; init; }

        /// <summary>进程名 / 开机启动项名字。</summary>
        public required string AppName { get; init; }

        /// <summary>是否连本地数据一起删（用户在卸载提示里的选择）。</summary>
        public bool DeleteData { get; init; }
    }

    /// <summary>
    /// 卸载助手（非商店版）：生成并启动 PowerShell 卸载脚本——
    /// 停止应用 → 删除开机启动项 → 删除开始菜单快捷方式 → 删除安装目录里属于自己的文件 →
    /// 按选择删除本地数据（%LOCALAPPDATA%\ShoreHue）→ 清理更新临时目录 → 删除脚本自身。
    /// 脚本放 %TEMP%（不在安装目录），应用退出后由独立进程执行。
    /// 商店（MSIX）版由系统负责卸载，不提供此入口。
    /// </summary>
    public static class UninstallHelper
    {
        /// <summary>生成并启动卸载脚本。返回是否成功启动（脚本随后会杀掉应用进程）。</summary>
        public static bool LaunchUninstall(bool deleteData)
        {
            try
            {
                if (AppPaths.IsPackaged) return false;

                string ps = Path.Combine(Path.GetTempPath(), "uninstall_shorehue.ps1");
                var ctx = new UninstallContext
                {
                    ExeDir = AppContext.BaseDirectory,
                    // ★ 数据目录走 AppPaths.DataRoot，不再硬编码 —— 与其他代码同一口径（也便于测试注入）
                    DataDir = AppPaths.DataRoot,
                    LnkPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ShoreHue.lnk"),
                    TempUpdateDir = Path.Combine(Path.GetTempPath(), "ShoreHueUpdate"),
                    ScriptPath = ps,
                    LogFile = Path.Combine(Path.GetTempPath(), "uninstall_shorehue.log"),
                    DesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    AppName = "ShoreHue",
                    DeleteData = deleteData
                };

                File.WriteAllText(ps, BuildScript(ctx), new UTF8Encoding(true));

                Process.Start(new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + ps + "\"")
                {
                    UseShellExecute = true,
                    CreateNoWindow = true
                });
                return true;
            }
            catch (Exception ex)
            {
                LogManager.Error("启动卸载脚本失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 生成卸载脚本正文（纯函数：同样的输入永远得到同样的脚本文本，便于测试直接执行它）。
        /// </summary>
        internal static string BuildScript(UninstallContext ctx)
        {
            string nl = Environment.NewLine;
            // 属于本程序、可以放心删的文件（见下面第 3 段的说明：没有安装清单，只能按名字前缀认）
            var ownedFiles = OwnedFilesIn(ctx.ExeDir);

            return
                // ★ 0) 日志函数：脚本由 powershell.exe（Windows PowerShell 5.1）执行，
                //    而它的 Add-Content 默认按**系统 ANSI**（简中=GBK）写文件 ——
                //    沙盘实测：日志里的中文在 UTF-8 环境下读出来是乱码。
                //    所以统一走 .NET 写「无 BOM 的 UTF-8」；日志写失败绝不能影响卸载本身，故 try/catch。
                "function Log([string]$m) { try { [System.IO.File]::AppendAllText('" + Escape(ctx.LogFile) + "', $m + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false))) } catch { } }" + nl +
                // ★ 1) 停止应用并等待进程真正退出（轮询，最多 5 秒；不再固定睡 800ms——
                //    句柄未释放时立即删目录会静默失败、残留文件）
                "Stop-Process -Name " + ctx.AppName + " -Force -ErrorAction SilentlyContinue" + nl +
                "for ($i = 0; $i -lt 10 -and (Get-Process -Name " + ctx.AppName + " -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }" + nl +
                // 2) 删除开机启动项 + 开始菜单快捷方式
                "Remove-ItemProperty -Path 'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run' -Name '" + Escape(ctx.AppName) + "' -ErrorAction SilentlyContinue" + nl +
                "Remove-Item -LiteralPath '" + Escape(ctx.LnkPath) + "' -Force -ErrorAction SilentlyContinue" + nl +
                // ★ 3) 安装目录：**只删「确定属于自己」的文件，从不递归删目录**。
                //    为什么不再"有 dll 就整套删"：那是在赌"这个目录是我独占的" ——
                //    而用户完全可能把整个 zip **摊在 Downloads / 桌面根目录**里，
                //    隔离沙盘实测过：那一下会把整个下载夹删掉。
                //    成熟安装器（Inno Setup 的 unins000.dat、MSI 的组件清单）靠**安装清单**
                //    做到"既删干净又不误删"，前提是安装时写了清单；ShoreHue 没有安装步骤、
                //    拿不到清单，所以选**保守一侧**：误删用户文件的代价远大于留几个残留 dll。
                //    （若将来 publish 时生成并嵌入一份文件清单，这里可升级成"按清单删"。）
                "$exeDir = '" + Escape(ctx.ExeDir) + "'" + nl +
                "$owned = @(" + string.Join(", ", ownedFiles.ConvertAll(f => "'" + Escape(f) + "'")) + ")" + nl +
                "foreach ($f in $owned) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }" + nl +
                "if (Test-Path -LiteralPath $exeDir) {" + nl +
                "  if (-not (Get-ChildItem -LiteralPath $exeDir -Force -ErrorAction SilentlyContinue)) {" + nl +
                "    Remove-Item -LiteralPath $exeDir -Force -ErrorAction SilentlyContinue" + nl +
                "  } else {" + nl +
                "    $left = (Get-ChildItem -LiteralPath $exeDir -Force -ErrorAction SilentlyContinue | Measure-Object).Count" + nl +
                "    Log ('[ShoreHue] 安装目录里还有 ' + $left + ' 项未清理（不是 ShoreHue 自己的文件，已原样保留）：' + $exeDir)" + nl +
                // ★ 只写日志用户看不到（日志在 %TEMP%），所以**在桌面放一份说明**告诉他去哪清理。
                //   仅在"确实有残留"时才写 —— 干净卸载不往桌面丢任何东西。
                "    try {" + nl +
                "      $desk = '" + Escape(ctx.DesktopDir) + "'" + nl +
                "      if ($desk) {" + nl +
                "        $note = Join-Path $desk '" + Escape(ctx.AppName) + "-卸载残留说明.txt'" + nl +
                "        $body = @(" + nl +
                "          'ShoreHue 已卸载。'," + nl +
                "          ''," + nl +
                "          ('安装目录里还有 ' + $left + ' 项不是 ShoreHue 自己的文件（可能是它的第三方依赖，也可能是你自己的文件）。')," + nl +
                "          '卸载程序没有删它们，也没有删这个目录 —— 因为继续删下去就可能误删你的东西。'," + nl +
                "          ''," + nl +
                "          ('安装目录：' + $exeDir)," + nl +
                "          ''," + nl +
                "          '你可以这样处理：'," + nl +
                "          '  · 如果那是你自己的目录（比如下载夹、桌面、某个项目文件夹）→ 忽略本说明即可，ShoreHue 的部分已经删干净了；'," + nl +
                "          '  · 如果是你专门为 ShoreHue 建的目录 → 确认里面没有别的东西后，整个文件夹删掉即可。'," + nl +
                "          ''," + nl +
                "          '看过之后，本文件也可以直接删除。'" + nl +
                "        )" + nl +
                "        Set-Content -LiteralPath $note -Value $body -Encoding UTF8" + nl +
                "      }" + nl +
                "    } catch { }" + nl +
                "  }" + nl +
                "}" + nl +
                // 4) 数据目录 / 更新临时目录（都是应用自己的专用目录，递归删安全），带重试；
                //    ★ 删除失败以前是**静默**的（用户以为清干净了、其实目录还在）—— 现在写进日志
                "$dirs = @(" + (ctx.DeleteData ? "'" + Escape(ctx.DataDir) + "', " : "") + "'" + Escape(ctx.TempUpdateDir) + "')" + nl +
                "foreach ($d in $dirs) {" + nl +
                "  if (Test-Path -LiteralPath $d) {" + nl +
                "    $ok = $false" + nl +
                "    for ($i = 0; $i -lt 5; $i++) {" + nl +
                "      try { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction Stop; $ok = $true; break }" + nl +
                "      catch { Start-Sleep -Milliseconds 600 }" + nl +
                "    }" + nl +
                "    if (-not $ok) { Log ('[ShoreHue] 删除失败（可能被占用）：' + $d) }" + nl +
                "  }" + nl +
                "}" + nl +
                // 5) 自删脚本（日志留着，便于排查"为什么没删干净"）
                "Remove-Item -LiteralPath '" + Escape(ctx.ScriptPath) + "' -Force -ErrorAction SilentlyContinue";
        }

        /// <summary>
        /// 列出安装目录里**确定属于本程序**的文件（卸载时可安全删除的那部分）。
        ///
        /// 判据是文件名前缀 <c>ShoreHue.</c> —— 这是"没有安装清单"时的保守退路：
        /// 发布物里的第三方依赖（`Microsoft.*` / `NAudio.*` / `System.*` 等）**一律不动**，
        /// 卸载后可能残留几十个 dll，但**绝不会误删用户自己的文件**。
        ///
        /// ★ 为什么不"整套递归删目录"：那等于赌"这个目录是我独占的"，而用户完全可能把
        ///   整个 zip 摊在 `Downloads\` / 桌面根目录里。成熟安装器（Inno 的 unins000.dat、
        ///   MSI 的组件清单）能既删干净又不误删，是因为**安装时写了清单**；ShoreHue 没有
        ///   安装步骤，所以只能选保守一侧。（将来若 publish 时生成/嵌入文件清单，这里可升级。）
        /// </summary>
        internal static List<string> OwnedFilesIn(string dir)
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(dir)) return list;
                foreach (string f in Directory.GetFiles(dir))
                {
                    if (Path.GetFileName(f).StartsWith("ShoreHue.", StringComparison.OrdinalIgnoreCase))
                        list.Add(f);
                }
            }
            catch (Exception ex)
            {
                // 读不动就少删（保守方向）：宁可有残留，也不误删
                LogManager.Warning($"[卸载] 枚举安装目录失败（将少删一些）：{dir} — {ex.Message}");
            }
            return list;
        }

        private static string Escape(string s) => s.Replace("'", "''");
    }
}
