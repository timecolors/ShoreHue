using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Infrastructure.Utils;

namespace ShoreHue.Infrastructure.WinApi
{
    /// <summary>
    /// 自动更新（GitHub Releases）：
    /// 检查最新 Release，下载资产（zip/exe），SHA256 校验，解压出 exe，
    /// 通过 PowerShell 脚本等主进程退出后替换并重启。
    /// </summary>
    public static class UpdateService
    {
        // ★ 更新源（GitHub Releases）写死在这里：发布时把 ShoreHue.exe 或 zip 上传到
        //   github.com/{GitHubOwner}/{GitHubRepo}/releases，tag 用版本号（如 v1.0.1）。
        public const string GitHubOwner = "timecolors";
        public const string GitHubRepo = "ShoreHue";

        public sealed class UpdateInfo
        {
            public Version Version { get; set; } = new(0, 0, 0);
            public string Tag { get; set; } = "";
            public string DownloadUrl { get; set; } = "";
            public string FileName { get; set; } = "";
            public string Sha256 { get; set; } = "";
            public string Notes { get; set; } = "";
        }

        /// <summary>检查 GitHub 最新 Release；无更新或检查失败返回 null。</summary>
        public static async Task<UpdateInfo?> CheckForUpdateAsync(Version current)
        {
            if (AppPaths.IsPackaged) return null; // 商店版由 Microsoft Store 负责更新
            if (string.IsNullOrWhiteSpace(GitHubOwner) || string.IsNullOrWhiteSpace(GitHubRepo)) return null;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ShoreHue");

                string url = $"https://api.github.com/repos/{Uri.EscapeDataString(GitHubOwner)}/{Uri.EscapeDataString(GitHubRepo)}/releases/latest";
                string json = await http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                string? body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
                if (string.IsNullOrEmpty(tag)) return null;

                Version? version = ParseVersion(tag);
                if (version == null || version <= current) return null;

                // ★ 这里必须**优先精确挑 ShoreHue.exe**，不能"取第一个 .exe/.zip"（见 PickUpdateAsset 的说明）
                string? assetUrl = null;
                string? assetName = null;
                if (root.TryGetProperty("assets", out var assets))
                {
                    var candidates = (from a in assets.EnumerateArray()
                                      let name = a.TryGetProperty("name", out var n) ? n.GetString() : null
                                      where !string.IsNullOrEmpty(name)
                                      select (Name: name!,
                                              Url: a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null))
                                     .ToList();
                    string? chosen = PickUpdateAsset(candidates.Select(c => c.Name));
                    if (chosen != null)
                    {
                        var hit = candidates.First(c => string.Equals(c.Name, chosen, StringComparison.Ordinal));
                        assetUrl = hit.Url;
                        assetName = hit.Name;
                    }
                }
                if (string.IsNullOrEmpty(assetUrl)) return null;

                return new UpdateInfo
                {
                    Version = version,
                    Tag = tag,
                    DownloadUrl = assetUrl,
                    FileName = assetName ?? "ShoreHue.zip",
                    Sha256 = ParseSha256(body),
                    Notes = body ?? ""
                };
            }
            catch (Exception ex)
            {
                // 返回 null = 界面显示"已是最新"（用户以为检查过了），失败原因必须留痕
                LogManager.Warning($"[更新] 解析更新信息失败（按无更新处理）：{ex.Message}");
                return null;
            }
        }

        /// <summary>下载失败的原因（用于给用户**可理解、有出路**的提示，而不是一律"下载失败"）。</summary>
        public enum DownloadFailure
        {
            None = 0,
            /// <summary>网络/IO 失败（用户重试可能成功）。</summary>
            Network,
            /// <summary>发布信息里没有完整性校验值 → 这是**发布流程**的问题，重试永远不会成功。</summary>
            MissingHash,
            /// <summary>下载内容的校验值不匹配（可能被篡改，或下载损坏）。</summary>
            HashMismatch,
        }

        /// <summary>
        /// 下载更新包到临时目录并按 SHA256 校验，返回文件路径。
        /// ★ 失败时同时返回**原因**：以前只返回 null，调用方一律显示"下载失败，请检查网络后重试"——
        ///   而"发布说明里没有校验值"这类失败重试一万次也不会成功，用户会一直在网络里找原因。
        /// </summary>
        /// <param name="allowMissingHash">
        /// 允许在"发布说明没有校验值"时跳过校验（由调用方在**如实告知用户风险并得到同意后**传入）。
        /// ★ 只对"缺失校验值"生效；**校验值不匹配任何时候都不放行** ——
        ///   那意味着下载内容与发布者声明不符（篡改或损坏），让用户绕过等于让他装一个已知被改过的文件。
        /// </param>
        public static async Task<(string? Path, DownloadFailure Failure)> DownloadUpdateAsync(
            UpdateInfo info, bool allowMissingHash = false)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "ShoreHueUpdate");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, SanitizeFileName(info.FileName));

                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ShoreHue");
                byte[] bytes = await http.GetByteArrayAsync(info.DownloadUrl);

                // ★ 默认 fail-closed：Release 说明里没写哈希时**拒绝安装**，不能静默跳过校验。
                //   旧实现 `if (!string.IsNullOrEmpty(info.Sha256))` → 没有哈希就直接下载安装，
                //   于是"发布说明里少写一行"就等价于关掉了完整性校验（下载链路可被中间人替换）。
                if (string.IsNullOrEmpty(info.Sha256))
                {
                    if (!allowMissingHash)
                    {
                        LogManager.Error(
                            "[Update] 发布信息里没有 SHA256，已拒绝下载安装（完整性无法校验）。请在 Release 说明中补上校验值。");
                        return (null, DownloadFailure.MissingHash);
                    }
                    // 用户知情后选择继续：跳过校验，但把这件事记录在案（事后排查"为什么装了未校验的包"）
                    LogManager.Warning("[Update] 缺少 SHA256，已按用户确认跳过完整性校验并继续安装。");
                }
                else
                {
                    string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    if (!string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        LogManager.Warning(
                            $"[Update] SHA256 校验失败: 期望 {info.Sha256} 实际 {hash}");
                        return (null, DownloadFailure.HashMismatch);
                    }
                }

                await File.WriteAllBytesAsync(file, bytes);
                return (file, DownloadFailure.None);
            }
            catch (Exception ex)
            {
                LogManager.Error("下载更新失败", ex);
                return (null, DownloadFailure.Network);
            }
        }

        /// <summary>从 zip 更新包中解压出 ShoreHue.exe；非 zip 直接返回原路径。</summary>
        public static async Task<string?> ExtractExeAsync(string packagePath)
        {
            try
            {
                if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    string extractDir = Path.Combine(Path.GetTempPath(), "ShoreHueUpdate", "extract");
                    if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                    Directory.CreateDirectory(extractDir);
                    ZipFile.ExtractToDirectory(packagePath, extractDir);

                    return FindFile(extractDir, "ShoreHue.exe");
                }
                return packagePath;
            }
            catch (Exception ex)
            {
                LogManager.Error("解压更新包失败", ex);
                return null;
            }
        }

        /// <summary>
        /// 应用更新：把新 exe 暂存到程序目录，生成 PowerShell 替换脚本并启动。
        /// 主进程退出后脚本完成替换并重启新版本。
        /// ★ 健壮性：替换失败不再静默——写 update_failed.txt，下次启动提示"仍为旧版本"；
        ///   成功才重启新 exe；脚本自身最后删除（含残留清理）。
        /// </summary>
        public static bool ApplyUpdate(string newExePath)
        {
            try
            {
                if (AppPaths.IsPackaged) return false; // 商店版不使用 GitHub 更新
                string exeDir = AppContext.BaseDirectory;
                string currentExe = Path.Combine(exeDir, "ShoreHue.exe");
                if (!File.Exists(newExePath) || !File.Exists(currentExe)) return false;

                string staged = Path.Combine(exeDir, "ShoreHue.new.exe");
                File.Copy(newExePath, staged, true);

                string ps = Path.Combine(exeDir, "apply_update.ps1");
                string failMarker = Path.Combine(exeDir, "update_failed.txt");
                string nl = Environment.NewLine;
                string script =
                    "Start-Sleep -Seconds 2" + nl +
                    "$exe = '" + EscapePs(currentExe) + "'" + nl +
                    "$new = '" + EscapePs(staged) + "'" + nl +
                    "$marker = '" + EscapePs(failMarker) + "'" + nl +
                    "for ($i = 0; $i -lt 10 -and (Get-Process -Name ShoreHue -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }" + nl +
                    "$ok = $false" + nl +
                    "for ($i = 0; $i -lt 5; $i++) { try { Copy-Item $new $exe -Force -ErrorAction Stop; $ok = $true; break } catch { Start-Sleep -Milliseconds 600 } }" + nl +
                    "if ($ok) {" + nl +
                    "  Remove-Item $new -ErrorAction SilentlyContinue" + nl +
                    "  Remove-Item $marker -ErrorAction SilentlyContinue" + nl +
                    "  Start-Process $exe" + nl +
                    "} else {" + nl +
                    "  Remove-Item $new -ErrorAction SilentlyContinue" + nl +
                    "  try { Set-Content -Path $marker -Value ('UPDATE_FAILED ' + (Get-Date)) -Encoding UTF8 } catch { }" + nl +
                    "}" + nl +
                    "Remove-Item '" + EscapePs(ps) + "' -ErrorAction SilentlyContinue";
                File.WriteAllText(ps, script, new UTF8Encoding(true));

                // ★ 修复：-File 参数必须真正拼接脚本路径（原字符串字面量写成 \" + ps + \"，
                //   ps 变量从未进入命令行参数 → 替换脚本从不执行，自动更新静默失败）
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
                LogManager.Error("应用更新失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 启动时清理更新残留（.new.exe / apply_update.ps1 / update_failed.txt）。
        /// 返回 true = 上次更新失败（调用方应提示用户仍为旧版本）；同时顺带清理残留文件。
        /// </summary>
        public static bool CleanupStaleFiles()
        {
            bool failed = false;
            try
            {
                if (AppPaths.IsPackaged) return false;
                string exeDir = AppContext.BaseDirectory;
                string marker = Path.Combine(exeDir, "update_failed.txt");
                failed = File.Exists(marker);

                foreach (var name in new[] { "ShoreHue.new.exe", "apply_update.ps1", "update_failed.txt" })
                {
                    string p = Path.Combine(exeDir, name);
                    // 尽力而为：残留文件删不掉不影响本次运行（下次启动还会再试）
                    try { if (File.Exists(p)) File.Delete(p); }
                    catch (Exception ex) { LogManager.Debug($"[更新] 清理更新残留文件失败（无害）{p}：{ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                // 读不到"上次更新失败"标记 → 启动时不提示用户（用户可能正卡在坏版本上）
                LogManager.Warning($"[更新] 检查更新失败标记失败（按正常处理）：{ex.Message}");
            }
            return failed;
        }

        internal static Version? ParseVersion(string tag)
        {
            string v = tag.TrimStart('v', 'V');
            int cut = v.IndexOfAny(new[] { '-', '+' });
            if (cut >= 0) v = v[..cut];
            return Version.TryParse(v, out var ver) ? ver : null;
        }

        /// <summary>
        /// 从 Release 的资产名里挑出用于自动更新的那个：**优先精确的 `ShoreHue.exe`**，
        /// 其次才回退到任意 `.exe` / `.zip`，都不匹配返回 null（调用方视为"没有更新包"）。
        ///
        /// ★ 为什么必须优先精确名（2026-09-26 加 zip 资产时发现）：发布正文里只写**一个** SHA256
        ///   （就是 exe 的），而客户端是 fail-closed 比对。本版起 Release 同时带 zip
        ///   （`ShoreHue-v1.x.y-win-x64.zip`）——按字母序 `ShoreHue-` 排在 `ShoreHue.` 前面，
        ///   若沿用"取第一个 .exe/.zip"，所有用户都会下到 zip、拿 exe 的哈希去比 →
        ///   校验不匹配 → 自动更新被安全地拒绝（功能静默坏掉，界面只会说"下载失败"）。
        ///   回退分支保留：将来若某版只发 zip、且正文里写的是该 zip 的哈希，它仍然可用。
        /// </summary>
        internal static string? PickUpdateAsset(IEnumerable<string> assetNames)
        {
            string? archive = null;
            foreach (string? name in assetNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (string.Equals(name, "ShoreHue.exe", StringComparison.OrdinalIgnoreCase)) return name;
                if (archive == null &&
                    (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                {
                    archive = name;
                }
            }
            return archive;
        }

        internal static string ParseSha256(string? body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            foreach (var line in body.Split('\n'))
            {
                int idx = line.IndexOf("SHA256", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                foreach (var part in line.Split(new[] { ':', '=', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.Length == 64 && part.All(Uri.IsHexDigit))
                        return part.ToLowerInvariant();
                }
            }
            return "";
        }

        private static string? FindFile(string dir, string name)
        {
            foreach (var f in Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories))
            {
                return f;
            }
            return null;
        }

        private static string EscapePs(string s) => s.Replace("'", "''");

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }
}