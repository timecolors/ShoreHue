using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Core.Models;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.UI.Widgets.Dynamic;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace ShoreHue.UI.Seabed
{
    /// <summary>
    /// 其他海床 · 预设/功能包（线上市场托管前的本地文件共享形态）：
    /// zip 包（.shpkg，旧版 .dbp 仍可导入）= manifest.json（元信息 + 权限标注）+ main.cs（源码）+ config.json。
    /// - 导出（= 上传前的打包）：用 WidgetPermissions.Detect 在导出时刻检测源码权限并写入 manifest；
    /// - 导入：重新检测权限（不信任包内声明，防篡改），有风险权限时由调用方弹窗提示用户确认后才写入。
    /// </summary>
    public static class SeabedPackage
    {
        /// <summary>包扩展名（ShoreHue Package）。旧版为 .dbp（DynamicBird 时代的缩写），仍可导入。</summary>
        public const string Extension = ".shpkg";

        /// <summary>兼容读取的旧扩展名（不用于导出）。</summary>
        public static readonly string[] LegacyExtensions = { ".dbp" };

        /// <summary>文件对话框过滤器（新格式在前，旧格式仍可选）。</summary>
        public const string DialogFilter = "ShoreHue 预设包 (*.shpkg)|*.shpkg|旧版预设包 (*.dbp)|*.dbp|所有文件|*.*";
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public sealed class ImportResult
        {
            public string Name = "未命名";
            public string Kind = "Config";          // Config/Widget/Panel/Category/Full
            public string? Category;                // 所属一级分类（小组件/面板功能/…）
            public string? BaseType;
            public string? ParentKey;
            public string? SourceKey;
            /// <summary>市场包 ID（形如 `登录名/短名`）。安装/导入时记住它，更新时才能自动填对 ID。</summary>
            public string? MarketId;
            public string Source = "";
            public string ConfigJson = "{}";
            public List<string> Permissions = new();
            public System.Collections.Generic.List<GitHubMarketService.PackageFile> ExtraFiles = new();
            public SettingsData? FullData;          // Kind==Full：整套预设数据
        }

        /// <summary>导出单预设（树中自定义项）为 .shpkg 包。成功返回 null，失败返回错误信息。</summary>
        public static string? ExportCustom(CustomPanelDefinition cp, string path)
        {
            try
            {
                using var fs = File.Create(path);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                var manifest = new Dictionary<string, object?>
                {
                    ["format"] = 1,
                    ["name"] = cp.Name,
                    ["kind"] = cp.Kind,
                    ["baseType"] = cp.BaseType,
                    ["parentKey"] = cp.ParentKey,
                    ["sourceKey"] = cp.SourceKey,
                    ["createdAt"] = cp.CreatedAt,
                    // ★ 导出（上传）时刻检测权限，随包下发
                    ["permissions"] = WidgetPermissions.Detect(cp.Source ?? ""),
                    // ★ 文件清单（下载端按此解包）
                    ["files"] = new System.Collections.Generic.List<string> { "main.cs" }
                };
                WriteEntry(zip, "manifest.json", JsonSerializer.Serialize(manifest, JsonOptions));
                WriteEntry(zip, "main.cs", cp.Source ?? "");
                WriteEntry(zip, "config.json", cp.ConfigJson ?? "{}");
                // ★ 多形态：把节点海床文件夹里的附加文件（.xaml/.xaml.cs 等）一并打包
                try
                {
                    string group = ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.MapCategoryToFolder(cp.Category);
                    string safeName = SanitizeFolderName(cp.Name);
                    string nodeDir = Path.Combine(ShoreHue.UI.Widgets.Dynamic.WidgetPluginStore.RootDir, group, safeName);
                    if (Directory.Exists(nodeDir))
                    {
                        foreach (var f in Directory.EnumerateFiles(nodeDir))
                        {
                            string fn = Path.GetFileName(f);
                            if (fn == "main.cs" || fn == "config.json" || fn == "manifest.json") continue;
                            if (fn.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                                || fn.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)
                                || fn.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                            {
                                WriteEntry(zip, fn, File.ReadAllText(f));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 收集附加文件失败 → 导出的包里会缺 .xaml/.cs（放流出去的包不完整），用户可见
                    LogManager.Warning($"[海床] 导出时读取附加文件失败（包内会缺文件）：{ex.Message}");
                }
                return null;
            }
            catch (Exception ex) { return "导出失败：" + ex.Message; }
        }

        /// <summary>导出整套预设为 .shpkg 包。成功返回 null，失败返回错误信息。</summary>
        public static string? ExportFullPreset(string presetName, SettingsData data, string path)
        {
            try
            {
                using var fs = File.Create(path);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                var manifest = new Dictionary<string, object?>
                {
                    ["format"] = 1,
                    ["name"] = presetName,
                    ["kind"] = "Full",
                    ["permissions"] = new List<string>()
                };
                WriteEntry(zip, "manifest.json", JsonSerializer.Serialize(manifest, JsonOptions));
                WriteEntry(zip, "config.json", JsonSerializer.Serialize(data, JsonOptions));
                return null;
            }
            catch (Exception ex) { return "导出失败：" + ex.Message; }
        }

        /// <summary>解析 .shpkg 包（兼容旧 .dbp）。失败返回 null 并给出 error。</summary>
        public static ImportResult? Import(string path, out string? error)
        {
            error = null;
            try
            {
                using var fs = File.OpenRead(path);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                string? manifestJsonRaw = ReadEntry(zip, "manifest.json");
                string manifestJson = manifestJsonRaw ?? "";
                if (string.IsNullOrEmpty(manifestJson)) { error = "包内缺少 manifest.json"; return null; }

                using var doc = JsonDocument.Parse(manifestJson);
                var root = doc.RootElement;
                var result = new ImportResult
                {
                    Name = GetStr(root, "name") ?? "未命名",
                    Kind = GetStr(root, "kind") ?? "Config",
                    Category = GetStr(root, "category"),
                    BaseType = GetStr(root, "baseType"),
                    ParentKey = GetStr(root, "parentKey"),
                    SourceKey = GetStr(root, "sourceKey"),
                    Source = ReadEntry(zip, "main.cs") ?? "",
                    ConfigJson = ReadEntry(zip, "config.json") ?? "{}"
                };
                // ★ 多形态：读取包内 main.cs/config.json 之外的条目（.xaml / .xaml.cs 等）
                foreach (var entry in zip.Entries)
                {
                    string en = entry.Name;
                    if (string.IsNullOrEmpty(en) || en == "main.cs" || en == "config.json" || en == "manifest.json") continue;
                    result.ExtraFiles.Add(new GitHubMarketService.PackageFile(en, ReadEntry(zip, en) ?? ""));
                }
                if (root.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in perms.EnumerateArray())
                    {
                        string? s = p.GetString();
                        if (!string.IsNullOrEmpty(s) && !result.Permissions.Contains(s)) result.Permissions.Add(s);
                    }
                }
                if (result.Kind == "Full")
                {
                    result.FullData = JsonSerializer.Deserialize<SettingsData>(result.ConfigJson);
                    // ★ 信任表不能由包注入：一条 TrustedPlugins 记录就能让受害机上任意外来包
                    //   在下次加载时跳过整个沙箱（ComputeTrust 第一步即 trustedByHash）。
                    //   只保留本机已有的同 id 同哈希记录。
                    if (result.FullData != null)
                        PluginTrustSanitizer.StripExternalTrust(result.FullData,
                            PluginTrustSanitizer.ReadLocalTrust(), $"包「{result.Name}」");
                }
                // ★ 导入时刻重新检测权限（不信任包内声明，防篡改）。
                //   必须把**所有会真正执行的文本**都算进去：XAML 形态的代码在 .xaml.cs 里，
                //   只看 main.cs 会把一个"main.cs 干净、.xaml.cs 联网"的包报成"没有检测到明显的能力需求"。
                string codeForPermissions = result.Source + "\n" +
                    string.Join("\n", result.ExtraFiles.Select(f => f.Content));
                if (!string.IsNullOrWhiteSpace(codeForPermissions))
                {
                    result.Permissions = WidgetPermissions.Detect(codeForPermissions);
                }
                return result;
            }
            catch (Exception ex) { error = "导入失败：" + ex.Message; return null; }
        }

        private static string SanitizeFolderName(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in (name ?? "").ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') sb.Append(ch);
                if (sb.Length >= 32) break;
            }
            return sb.Length >= 2 ? sb.ToString() : "unnamed";
        }

        private static string? GetStr(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static void WriteEntry(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name);
            using var sw = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false));
            sw.Write(content ?? "");
        }

        /// <summary>zip 条目读取，带大小上限（防恶意包塞超大文件内存炸弹）。</summary>
        private const int MaxEntryBytes = 2 * 1024 * 1024;   // 单条目 2MB 上限（源码/配置足够）

        private static string? ReadEntry(ZipArchive zip, string name)
        {
            var entry = zip.GetEntry(name);
            if (entry == null) return null;
            if (entry.Length > MaxEntryBytes) throw new InvalidOperationException("包内条目过大: " + name);
            using var sr = new StreamReader(entry.Open(), System.Text.Encoding.UTF8);
            return sr.ReadToEnd();
        }
    }
}
