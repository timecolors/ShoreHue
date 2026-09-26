// ==================== 海床树的「行模型 + 扫描器」（从 SeabedPage 抽出来） ====================
//
// 为什么抽出来（2026-09-13）：
//   ① `SeabedPage` 当时已 2400+ 行，扫描/筛选/行模型混在页面里；
//   ② 更实际的理由是**可测**：扫描与筛选是纯「文件系统 → 行」的活，抽出来就能无头断言，
//      而页面里只能靠人在界面上点。目录扁平化（把面板平铺到 面板\<id>）最需要的正是这套测试台 ——
//      "哪个目录算面板、哪个算配置组"必须能被测试钉住，而不是靠眼睛看树。
//
// 这里**不碰 UI**：只读文件系统、产出 `FlatNode`。高亮（编译错误/未启用/被预设覆盖）由页面后续设置。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ShoreHue.Core.Infrastructure.Logging;

namespace ShoreHue.UI.Seabed
{
    /// <summary>
    /// 海床树的一行。两种来源：**资源管理器行**（`FsPath != null`，对应真实文件/目录）
    /// 与**传统配置树行**（`FsPath == null`，由 `Node` 驱动；旧路径，扫描器不产出）。
    /// </summary>
    internal sealed class FlatNode
    {
        public ShoreHue.Core.Models.ConfigNode Node = null!;   // 仅由构建树的代码在构造后立即赋值
        public int Level;
        public bool IsAdd { get; set; }      // 每级末尾的"⊕ 新建同级"占位行
        public bool HasDelete { get; set; }  // 用户新建项：可删除（行末 ×）

        // ===== 文件资源管理器行（文件夹=真相：树显示 seabed 真实目录/文件） =====
        /// <summary>资源管理器行：选中项的绝对路径（文件或目录）。null = 传统配置树行。</summary>
        public string? FsPath;
        /// <summary>资源管理器行：是否为目录（false = 文件）。</summary>
        public bool FsIsDir;
        /// <summary>资源管理器行：内置标记（该目录/其父目录 manifest system=true → 删除前警告）。</summary>
        public bool FsIsSystem;
        /// <summary>资源管理器行：显示名（目录/文件名原样，不中文化）。</summary>
        public string? DisplayOverride;
        /// <summary>资源管理器行：所在功能目录的 manifest kind（Widget/Panel/Config/StatusProvider/Animation）。</summary>
        public string? FsKind;
        /// <summary>资源管理器行：所在功能目录的 manifest id（英文标识）。</summary>
        public string? FsManifestId;
        /// <summary>资源管理器行：是否为配置目录（常规/区域/面板/动画 下的 config.json 投影）。</summary>
        public bool FsIsConfigDir;

        public string Display => DisplayOverride != null
            ? DisplayOverride
            : IsAdd
                ? ""   // 占位行只显示 ⊕ 按钮，不再显示文字
                : Level switch
                {
                    0 => Node.Name,
                    1 => "▸ " + Node.Name,
                    _ => "• " + Node.Name
                };
        public double Indent => Level switch { 0 => 0, 1 => 14, _ => 30 };
        public System.Windows.Thickness IndentMargin => new System.Windows.Thickness(Indent, 0, 0, 0);

        // 高亮：编译报错 → 叉的红色；未启用的面板 → Windows 主题色；
        // 被预设覆盖（未启用）→ 灰色加删除线；否则默认
        public bool IsError { get; set; }
        public bool IsUnused { get; set; }
        public bool IsOverridden { get; set; }
        public bool IsApplied { get; set; }   // 该单预设当前处于"已应用"状态 → 高亮
        public System.Windows.Media.Brush TextBrush
        {
            get
            {
                if (IsError) return _errBrush;
                if (IsApplied) return _accentBrush;
                if (IsUnused) return _accentBrush;
                if (IsOverridden) return _dimBrush;
                return System.Windows.Media.Brushes.Black;
            }
        }
        public System.Windows.TextDecorationCollection? TextDecor => IsOverridden
            ? System.Windows.TextDecorations.Strikethrough
            : null;
        private static readonly System.Windows.Media.Brush _dimBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA0, 0xA0, 0xA0));
        private static readonly System.Windows.Media.Brush _errBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x60, 0x60));
        private static readonly System.Windows.Media.Brush _accentBrush = AccentBrush();

        /// <summary>Windows 主题色（随系统强调色变化），取不到时用蓝色。</summary>
        private static System.Windows.Media.Brush AccentBrush()
        {
            try
            {
                var settings = new Windows.UI.ViewManagement.UISettings();
                var c = settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
                return new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B));
            }
            catch
            {
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x90, 0xD9));
            }
        }
    }

    /// <summary>
    /// 海床目录 → 树行。**不依赖任何 UI**，因此可单测（见 `SeabedTreeScannerTests`）。
    /// 树的层级上限为 3（与既有行为一致，防深层目录把列表刷爆）。
    /// </summary>
    internal static class SeabedTreeScanner
    {
        internal const int MaxLevel = 3;

        /// <summary>
        /// 扫描海床根，产出树行。
        /// <paramref name="filter"/> 非空时为**筛选态**：全深度扫描、只列名字命中的项（扁平结果）。
        /// </summary>
        internal static List<FlatNode> Scan(string root, ISet<string> expandedDirs, string? filter)
        {
            var nodes = new List<FlatNode>();
            try
            {
                if (!Directory.Exists(root)) return nodes;

                if (IsFilterActive(filter))
                {
                    // 筛选态：**全深度**扫描，只列命中项，显示相对路径。
                    // ★ 为什么不做成"剪枝的树"：命中的文件可能在未展开的深层目录里，
                    //   剪枝要么看不见它、要么得把整条路径都撑开；扁平结果更接近 VSCode 的搜索列表，也更好点。
                    CollectFiltered(root, root, nodes, 0, filter!);
                }
                else
                {
                    foreach (string g in Directory.GetDirectories(root)
                                 .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                    {
                        nodes.Add(new FlatNode { Level = 0, DisplayOverride = Path.GetFileName(g), FsPath = g, FsIsDir = true });
                        if (expandedDirs.Contains(g)) ScanDir(g, nodes, 1, expandedDirs);
                    }
                }
            }
            catch (Exception ex)
            {
                // 整个树扫不出来 = 海床界面空白（文件夹还在，只是这次没读到）
                LogManager.Warning($"[海床] 扫描 seabed 目录失败（树可能为空）：{ex.Message}");
            }
            return nodes;
        }

        /// <summary>展开目录时列出其子目录 + 文件（文件仅当该目录本身已展开才显示）。</summary>
        private static void ScanDir(string dir, List<FlatNode> nodes, int level, ISet<string> expandedDirs)
        {
            if (level > MaxLevel) return;
            try
            {
                foreach (string sub in Directory.GetDirectories(dir)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    string kind = ReadManifestField(sub, "kind") ?? "";
                    string? sysField = ReadManifestField(sub, "system");
                    bool sys = string.Equals(sysField, "True", StringComparison.OrdinalIgnoreCase) || sysField == "true";
                    nodes.Add(new FlatNode
                    {
                        Level = level,
                        DisplayOverride = Path.GetFileName(sub),
                        FsPath = sub,
                        FsIsDir = true,
                        FsIsSystem = sys,
                        FsKind = kind,
                        FsIsConfigDir = kind == "Config",
                        FsManifestId = ReadManifestField(sub, "id"),
                        HasDelete = true
                    });
                    if (expandedDirs.Contains(sub) && level < MaxLevel) ScanDir(sub, nodes, level + 1, expandedDirs);
                }

                foreach (string f in Directory.GetFiles(dir)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    bool isManifest = string.Equals(Path.GetFileName(f), "manifest.json", StringComparison.OrdinalIgnoreCase);
                    nodes.Add(new FlatNode
                    {
                        Level = level,
                        DisplayOverride = Path.GetFileName(f),
                        FsPath = f,
                        FsIsDir = false,
                        FsKind = FsKindOf(dir),
                        HasDelete = !isManifest
                    });
                }
            }
            catch (Exception ex)
            {
                // 单个目录扫不动就少列它下面的内容（其余目录照常）
                LogManager.Debug($"[海床] 读取目录内容失败（跳过）{dir}：{ex.Message}");
            }
        }

        private static void CollectFiltered(string root, string dir, List<FlatNode> nodes, int depth, string filter)
        {
            if (depth > MaxLevel + 1) return;
            try
            {
                foreach (string sub in Directory.GetDirectories(dir)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    if (Matches(Path.GetFileName(sub), filter))
                    {
                        string kind = ReadManifestField(sub, "kind") ?? "";
                        string? sysField = ReadManifestField(sub, "system");
                        bool sys = string.Equals(sysField, "True", StringComparison.OrdinalIgnoreCase) || sysField == "true";
                        nodes.Add(new FlatNode
                        {
                            Level = 0,                                   // 扁平显示：不做层级缩进
                            DisplayOverride = RelToRoot(root, sub),
                            FsPath = sub,
                            FsIsDir = true,
                            FsIsSystem = sys,
                            FsKind = kind,
                            FsIsConfigDir = kind == "Config",
                            FsManifestId = ReadManifestField(sub, "id"),
                            HasDelete = true
                        });
                    }
                    CollectFiltered(root, sub, nodes, depth + 1, filter);   // 命中与否都继续往下找
                }

                foreach (string f in Directory.GetFiles(dir)
                             .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
                {
                    if (!Matches(Path.GetFileName(f), filter)) continue;
                    bool isManifest = string.Equals(Path.GetFileName(f), "manifest.json", StringComparison.OrdinalIgnoreCase);
                    nodes.Add(new FlatNode
                    {
                        Level = 0,
                        DisplayOverride = RelToRoot(root, f),
                        FsPath = f,
                        FsIsDir = false,
                        FsKind = FsKindOf(dir),
                        HasDelete = !isManifest
                    });
                }
            }
            catch (Exception ex)
            {
                // 单个目录扫不动就少列它下面的内容（其余照常）
                LogManager.Debug($"[海床] 筛选时读取目录失败（跳过）{dir}：{ex.Message}");
            }
        }

        /// <summary>筛选词是否生效（空/空白 = 不筛选）。</summary>
        internal static bool IsFilterActive(string? filter) => !string.IsNullOrWhiteSpace(filter);

        /// <summary>名字是否命中筛选词（空筛选词 = 全通过）。</summary>
        internal static bool Matches(string name, string? filter)
        {
            string q = (filter ?? "").Trim();
            if (q.Length == 0) return true;
            return name.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        }

        /// <summary>相对海床根的路径（筛选结果里用来区分同名文件）。</summary>
        internal static string RelToRoot(string root, string full)
        {
            try { return Path.GetRelativePath(root, full); }
            catch { return full; }
        }

        /// <summary>目录的 manifest kind（无 manifest 时按父目录推断：小组件→Widget、面板功能→Panel、动画→Animation、状态栏→StatusProvider）。</summary>
        internal static string? FsKindOf(string dir)
        {
            string? k = ReadManifestField(dir, "kind");
            if (!string.IsNullOrEmpty(k)) return k;
            string group = Path.GetFileName(Path.GetDirectoryName(dir) ?? "") ?? "";
            return group switch
            {
                "小组件" => "Widget",
                "面板功能" => "Panel",
                "动画" => "Animation",
                "状态栏" => "StatusProvider",
                _ => null
            };
        }

        /// <summary>
        /// 该路径是否位于"内置（system: true）"目录之下 —— 向上最多查 4 层 manifest。
        /// 用于删除前的警告：内置项删了可能让应用行为异常。
        /// 从页面搬来这里，是因为它只读 manifest、与 UI 无关，因此可单测。
        /// </summary>
        internal static bool IsUnderSystemDir(string path, string root)
        {
            string dir = path;
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir) && dir.Length > root.Length; i++)
            {
                if (string.Equals(ReadManifestField(dir, "system"), "true", StringComparison.OrdinalIgnoreCase)) return true;
                dir = Path.GetDirectoryName(dir) ?? "";
            }
            return false;
        }

        internal static string? ReadManifestField(string dir, string name)
        {
            try
            {
                string mf = Path.Combine(dir, "manifest.json");
                if (!File.Exists(mf)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(mf));
                if (doc.RootElement.TryGetProperty(name, out var v))
                    return v.ValueKind == JsonValueKind.String ? v.GetString()
                        : v.ValueKind == JsonValueKind.True ? "true"
                        : v.ValueKind == JsonValueKind.False ? "false" : v.GetRawText();
            }
            catch (Exception ex)
            {
                // 尽力而为：manifest 坏了或字段类型怪 → 当"没有该字段"
                LogManager.Debug($"[海床] 读取 manifest 字段失败 dir={dir} name={name}：{ex.Message}");
            }
            return null;
        }
    }
}
