using System;
using ShoreHue.Core.Infrastructure.Logging;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ShoreHue.Infrastructure.WinApi
{
    /// <summary>
    /// Windows Defender（Microsoft Defender）扫描封装：
    /// 导入「其他海床」包前用 MpCmdRun 扫描，捕获嵌入的已知威胁（恶意二进制/脚本特征）。
    /// 诚实定位：源码类攻击（C# 文本）无特征可匹配，Defender 扫不出恶意意图——
    /// 这只是"已扫描"保证层 + 抓嵌入载荷，真正的安全边界是 WidgetCompiler 编译期沙箱。
    /// </summary>
    public static class DefenderScanner
    {
        public enum ScanResult { Clean, ThreatFound, Unavailable }

        public static async Task<(ScanResult Result, string Detail)> ScanFileAsync(string path)
        {
            string? exe = FindMpCmdRun();
            if (exe == null)
            {
                return (ScanResult.Unavailable, "未找到 Windows Defender（可能使用第三方杀软，跳过扫描）");
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-Scan -ScanType 3 -File \"" + path + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return (ScanResult.Unavailable, "Defender 启动失败");
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await proc.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // 尽力而为：超时后杀掉扫描进程；杀不掉也不影响返回值（已经按"不可用"处理并会在界面提示）
                    try { proc.Kill(); }
                    catch (Exception kex) { LogManager.Debug($"[Defender] 结束超时的扫描进程失败（无害）：{kex.Message}"); }
                    return (ScanResult.Unavailable, "Defender 扫描超时");
                }
                string output = await outTask + Environment.NewLine + await errTask;

                // ★ 判定依据**不能**是本地化输出文本。
                //   MpCmdRun 在中文 Windows 上打印的是中文（"发现威胁:"之类），
                //   旧实现正则匹配英文 "Threats Found:"，匹配不到就当 0 → **把带毒包报成"未发现已知威胁"**
                //   （本应用的主要语言恰好是中文，这条路径在最常见的环境下就是坏的）。
                //   退出码才是稳定契约：0 = 未发现威胁，2 = 发现威胁。
                int exitCode = proc.ExitCode;
                if (exitCode == 2)
                {
                    // 尽量从输出里再捞一个数字用于提示；捞不到也给通用文案
                    var m2 = Regex.Match(output, @"(\d+)\s*(?:threats?|个威胁|威胁)", RegexOptions.IgnoreCase);
                    string detail = m2.Success
                        ? "Windows Defender 检出 " + m2.Groups[1].Value + " 个威胁"
                        : "Windows Defender 检出威胁（退出码 2）";
                    return (ScanResult.ThreatFound, detail);
                }
                if (exitCode != 0)
                {
                    // 其它非零退出码（参数错误/引擎不可用…）：**不能**当作"干净"，
                    // 否则一次扫描失败就会让用户以为已经查过了。
                    return (ScanResult.Unavailable,
                        $"Defender 扫描未正常完成（退出码 {exitCode}），本次未获得有效结论");
                }
                return (ScanResult.Clean, "Windows Defender 未发现已知威胁");
            }
            catch (Exception ex)
            {
                return (ScanResult.Unavailable, "Defender 扫描失败: " + ex.Message);
            }
        }

        private static string? FindMpCmdRun()
        {
            var candidates = new List<string>();
            try
            {
                candidates.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Windows Defender", "MpCmdRun.exe"));
            }
            catch (Exception ex) { LogManager.Debug($"[Defender] 候选路径不可用（跳过）：{ex.Message}"); }
            try
            {
                string pd = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Microsoft", "Windows Defender", "Platform");
                if (Directory.Exists(pd))
                {
                    foreach (var dir in Directory.GetDirectories(pd).OrderByDescending(d => d))
                    {
                        candidates.Add(Path.Combine(dir, "MpCmdRun.exe"));
                    }
                }
            }
            catch (Exception ex) { LogManager.Debug($"[Defender] 枚举候选目录失败（跳过）：{ex.Message}"); }
            return candidates.FirstOrDefault(File.Exists);
        }
    }
}
