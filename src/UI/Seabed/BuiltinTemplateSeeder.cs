using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Infrastructure.Utils;
using ShoreHue.UI.Widgets.Dynamic;

namespace ShoreHue.UI.Seabed
{
    /// <summary>
    /// 内置模板落盘器（方案B：文件夹=海床真相源）：
    /// 首次运行/重装时，把内置小组件与面板功能的模板写入 seabed/ 对应位置，带 system 标记 + templateVersion。
    /// ★ 用户改过的文件一律不覆盖，**并且内置模板升级时也只升级"用户没动过的那份"**：
    ///   落盘时把每个文件的内容哈希记进 manifest 的 seededFiles；
    ///   下次启动若磁盘哈希 == 落盘哈希（= 用户没动）而源码已变 → 覆盖升级；
    ///   磁盘哈希对不上（用户改过）→ 一个字节都不碰。
    ///   没有这条记录之前的行为是"只要文件存在就永不覆盖" —— 那会让运行时副本**永久冻结**在首次落盘的版本上，
    ///   实测咬过一次：给内置件补 IWidgetFooter 后，从文件夹加载的计时器页脚直接消失（编译能过、日志全绿）。
    /// 删除后不补（用户明确删的）。
    /// </summary>
    public static class BuiltinTemplateSeeder
    {
        /// <summary>当前内置模板版本（每次内置功能更新时 +1，触发未改文件刷新）。</summary>
        public const int TemplateVersion = 1;

        /// <summary>小组件清单：id → (中文名, 描述)。</summary>
        private static readonly Dictionary<string, (string Name, string Desc)> WidgetMeta = new()
        {
            ["calculator"] = ("计算器", "ShoreHue 内置计算器：标准 / 科学 / 程序员。"),
            ["timer"] = ("计时器", "ShoreHue 内置计时器：倒计时 / 正计时 / 闹钟。"),
            ["clipboard"] = ("剪贴板", "ShoreHue 内置剪贴板历史：复制记录 / 搜索 / 固定。"),
            ["note"] = ("便签", "ShoreHue 内置便签：多色 / 编辑 / 置顶。"),
            ["textai"] = ("划词翻译", "划词翻译：选中文本翻译 / 总结 / 解释。"),
            ["web"] = ("网页工具", "网页工具：内置浏览器，可导航任意网址。")
        };

        /// <summary>面板功能清单：key → (中文名, 描述)。</summary>
        private static readonly Dictionary<string, (string Name, string Desc)> PanelMeta = new()
        {
            ["panel-notification"] = ("通知坞", "系统通知列表：查看 / 点击打开来源应用。"),
            ["panel-recent"] = ("最近使用", "最近文件 / 应用 / 网页快速访问。"),
            ["panel-quicksettings"] = ("快捷设置", "WiFi / 蓝牙 / 热点 / 性能模式快速开关。"),
            ["panel-taskbar-feature"] = ("任务栏增强", "任务栏快捷方式与窗口标签。"),
            ["panel-ai"] = ("AI 面板", "AI 助手面板：聊天 / 划词。"),
            ["panel-apphelper"] = ("应用辅助", "媒体控制 / 窗口镜像 / 本地视频播放。"),
            ["panel-windowcontrol"] = ("窗口控制", "窗口操作：最小化 / 最大化 / 关闭 / 置顶。")
        };

        /// <summary>执行落盘（应用启动时调用一次）。</summary>
        public static void Seed()
        {
            try
            {
                var root = WidgetPluginStore.RootDir;
                if (!Directory.Exists(root)) Directory.CreateDirectory(root);

                // 1) 面板功能（纯代码模板，自包含）
                SeedPanels(root);

                // 2) 小组件（从项目源文件复制 XAML + cs）
                SeedWidgets(root);

                // 3) 配置节点（按设置页签分组：常规/区域/面板/动画 → <分组>/[<二级名>/]<叶子名>/config.json）
                //    ★ 文件夹=树的镜像：树里每个配置叶子都有文件夹投影
                SeedConfigNodes(root);

                // 4) 树驱动清理：删除不在当前树投影里的 system 配置目录
                //    （旧分组 面板设计/外观/交互/状态栏、改名残留如 小鸟依人、已删叶子等，全部按新树抹平）
                RemoveStaleConfigProjections(root);
            }
            catch (Exception ex)
            {
                // 内置模板没落盘 = 海床里看不到内置镜像副本（用户可见的功能缺失），必须留痕
                LogManager.Error($"[海床] 内置模板落盘失败（seabed 里会缺内置副本）：{ex.Message}", ex);
            }
        }

        /// <summary>删除不在当前树投影中的 system 配置目录（按 分组/二级/三级 显示名路径匹配；最多 3 层）。</summary>
        private static void RemoveStaleConfigProjections(string root)
        {
            try
            {
                // 期望路径集合：分组(一级 Category)/二级显示名[/三级显示名]（配置叶子用显示名做目录）
                var expected = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                var tree = ShoreHue.UI.Seabed.ConfigTreeBuilder.Build();
                foreach (var g in tree.Children)
                {
                    string gf = Sanitize(g.Category);
                    if (string.IsNullOrEmpty(gf)) continue;
                    foreach (var c2 in g.Children)
                    {
                        if (c2.Children.Count == 0)
                            expected.Add(gf + "/" + Sanitize(c2.Name));
                        else
                            foreach (var c3 in c2.Children)
                                expected.Add(gf + "/" + Sanitize(c2.Name) + "/" + Sanitize(c3.Name));
                    }
                }
                RemoveStaleProjectionsRecursive(root, "", expected);

                // 清理历史遗留的空一级目录（旧分组 面板设计/外观/交互/状态栏 等在投影删除后变空 → 一并移除，
                //   保持文件夹顶层与当前分类一致：小组件/面板功能 + 常规/区域/面板/动画）
                string[] currentGroups = { "常规", "区域", "面板", "动画" };
                foreach (var d in Directory.GetDirectories(root))
                {
                    string dn = Path.GetFileName(d);
                    if (System.Array.IndexOf(currentGroups, dn) >= 0) continue;
                    // 尽力而为：空目录删不掉只影响整洁度（下次启动还会再试）
                    try { DeleteEmptyDirsRecursive(d); }
                    catch (Exception ex) { LogManager.Debug($"[海床] 清理空目录失败（无害）{d}：{ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                // 只影响整洁度：旧投影目录会残留，功能不受影响（故 Debug 级）
                LogManager.Debug($"[海床] 清理过期配置投影失败（残留不影响功能）：{ex.Message}");
            }
        }

        /// <summary>递归删除空目录（仅当整棵子树为空）。</summary>
        private static void DeleteEmptyDirsRecursive(string dir)
        {
            foreach (var sd in Directory.GetDirectories(dir))
            {
                // 尽力而为：删不掉就留着（不影响功能）
                try { DeleteEmptyDirsRecursive(sd); }
                catch (Exception ex) { LogManager.Debug($"[海床] 清理子目录失败（无害）{sd}：{ex.Message}"); }
            }
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }

        private static void RemoveStaleProjectionsRecursive(string dir, string rel, System.Collections.Generic.HashSet<string> expected)
        {
            foreach (var d in Directory.GetDirectories(dir))
            {
                string name = Path.GetFileName(d);
                string childRel = string.IsNullOrEmpty(rel) ? name : rel + "/" + name;
                string mf = Path.Combine(d, "manifest.json");
                if (File.Exists(mf))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(mf));
                        var ro = doc.RootElement;
                        bool isSystem = ro.TryGetProperty("system", out var s) && s.ValueKind == JsonValueKind.True;
                        string? kind = ro.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                        // 仅清理 system=true 且 kind=Config 的树投影（用户代码/内置源码目录一律不碰）
                        if (isSystem && kind == "Config" && !expected.Contains(childRel))
                        {
                            Directory.Delete(d, true);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        // manifest 坏了 → 判断不出是不是 system 投影 → 保守不删（只影响整洁度）
                        LogManager.Debug($"[海床] 读取投影 manifest 失败（该目录跳过清理）{mf}：{ex.Message}");
                    }
                }
                RemoveStaleProjectionsRecursive(d, childRel, expected);
            }
        }

        /// <summary>删除历史遗留的 widget-* 系统占位目录（最多 3 层：分组/二级/叶子）。</summary>
        private static void RemoveStaleWidgetStubs(string root)
        {
            // 只影响整洁度：历史占位目录残留不影响功能
            try { RemoveStaleWidgetStubsRecursive(root, 0); }
            catch (Exception ex) { LogManager.Debug($"[海床] 清理历史 widget-* 占位目录失败（无害）：{ex.Message}"); }
        }

        private static void RemoveStaleWidgetStubsRecursive(string dir, int depth)
        {
            if (depth > 3) return;
            foreach (var d in Directory.GetDirectories(dir))
            {
                string mf = Path.Combine(d, "manifest.json");
                if (File.Exists(mf))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(mf));
                        var ro = doc.RootElement;
                        bool isSystem = ro.TryGetProperty("system", out var s) && s.ValueKind == JsonValueKind.True;
                        string? id = ro.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                        if (isSystem && id != null && id.StartsWith("widget-", StringComparison.Ordinal))
                        {
                            Directory.Delete(d, true);
                            continue;   // 已删除，不再递归其内部
                        }
                    }
                    catch (Exception ex)
                    {
                        // 同 E136：判断不出是不是内置占位 → 保守不删
                        LogManager.Debug($"[海床] 读取占位 manifest 失败（该目录跳过清理）{mf}：{ex.Message}");
                    }
                }
                RemoveStaleWidgetStubsRecursive(d, depth + 1);
            }
        }

        /// <summary>
        /// 把配置树（ConfigTreeBuilder）所有一级分组下的叶子节点落盘为 <分组>/<叶子名>/config.json：
        /// 内容 = 该节点绑定的 SettingsData 字段名 + 当前默认值（反射读取）。
        /// manifest.json 标记 kind=Config + system=true（内置投影，删除时警告；用户改过不覆盖）。
        /// </summary>
        private static void SeedConfigNodes(string root)
        {
            try
            {
                var data = new ShoreHue.Core.Services.Configuration.SettingsData();
                var props = typeof(ShoreHue.Core.Services.Configuration.SettingsData).GetProperties()
                    .ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);
                var tree = ShoreHue.UI.Seabed.ConfigTreeBuilder.Build();
                foreach (var group in tree.Children)
                {
                    string groupFolder = group.Category;   // 一级分组名 = 文件夹名（面板设计/动画/外观/交互/状态栏）
                    if (string.IsNullOrEmpty(groupFolder)) continue;
                    foreach (var child in group.Children)
                    {
                        if (child.Children.Count > 0)
                        {
                            // 二级分组有三级叶子：<分组>/<二级名>/<三级叶子名>/
                            foreach (var leaf in child.Children)
                            {
                                if (leaf.FieldNames == null || leaf.FieldNames.Count == 0) continue;
                                // 小组件叶子(widget-*)不是配置节点：真源在 小组件/<英文id>/（SeedWidgets 已落 XAML），
                                // 不再投影 config.json 占位——文件夹=真相：树里的条目在文件夹里必须对应真实资源文件
                                if (leaf.Key.StartsWith("widget-", StringComparison.Ordinal)) continue;
                                WriteConfigNode(root, groupFolder, Sanitize(child.Name), Sanitize(leaf.Name),
                                    leaf.Key, leaf.Name, groupFolder, leaf.FieldNames, props, data);
                            }
                        }
                        else
                        {
                            // 二级即叶子：<分组>/<叶子名>/
                            if (child.FieldNames == null || child.FieldNames.Count == 0) continue;
                            WriteConfigNode(root, groupFolder, "", Sanitize(child.Name),
                                child.Key, child.Name, groupFolder, child.FieldNames, props, data);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 配置节点的文件夹投影整块没落盘 → 海床树里会缺一片，用户可见
                LogManager.Error($"[海床] 配置节点投影落盘失败（海床里会缺文件夹）：{ex.Message}", ex);
            }
        }

        private static void WriteConfigNode(string root, string groupFolder, string subFolder, string nodeName,
            string key, string displayName, string category, System.Collections.Generic.List<string> fields,
            Dictionary<string, System.Reflection.PropertyInfo> props, ShoreHue.Core.Services.Configuration.SettingsData data)
        {
            try
            {
                if (nodeName.Length < 2) return;
                string dir = Path.Combine(root, groupFolder);
                if (!string.IsNullOrEmpty(subFolder)) dir = Path.Combine(dir, subFolder);
                dir = Path.Combine(dir, nodeName);
                Directory.CreateDirectory(dir);

                // config.json：该节点字段的当前默认值（用户可编辑；树↔文件夹还原时按字段名合并）
                var cfg = new Dictionary<string, object?>();
                foreach (var f in fields)
                {
                    if (!props.TryGetValue(f, out var p)) continue;
                    // 尽力而为：个别字段读不到默认值 → 该字段不进 config.json，其余字段照常
                    try { cfg[f] = p.GetValue(data); }
                    catch (Exception ex) { LogManager.Debug($"[海床] 读取字段默认值失败（跳过该字段）{f}：{ex.Message}"); }
                }
                string cfgPath = Path.Combine(dir, "config.json");
                string cfgJson = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                WriteTemplateFile(dir, "config.json", cfgJson);

                // manifest.json：节点元信息（树↔文件夹还原依据）
                string mf = Path.Combine(dir, "manifest.json");
                var manifest = new Dictionary<string, object?>
                {
                    ["id"] = key,
                    ["name"] = displayName,
                    ["category"] = category,
                    ["kind"] = "Config",
                    ["baseType"] = "Config",
                    ["parentKey"] = "",
                    ["sourceKey"] = "",
                    ["system"] = true,
                    ["templateVersion"] = TemplateVersion
                };
                WriteTemplateFile(dir, "manifest.json", JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[海床] 配置节点落盘失败（海床里缺该文件夹）{nodeName}：{ex.Message}");
            }
        }

        private static void SeedPanels(string root)
        {
            foreach (var kv in PanelMeta)
            {
                try
                {
                    if (!ShoreHue.UI.Seabed.BuiltinFeatureSources.Sources.TryGetValue(kv.Key, out var source)) continue;
                    if (string.IsNullOrWhiteSpace(source)) continue;
                    // ★ 扁平化后：面板功能与小组件都在 面板/ 下平铺、同级（见 docs\方案-海床目录扁平化.md）
                    string dir = Path.Combine(root, "面板", Sanitize(kv.Key));
                    Directory.CreateDirectory(dir);
                    var hashes = ReadSeededHashes(dir);
                    SyncTemplateFile(dir, "main.cs", source, hashes);
                    WriteManifest(dir, kv.Key, kv.Value.Name, "Panel", "面板功能", "1.0.0", kv.Key, hashes);
                }
                catch (Exception ex)
                {
                    LogManager.Warning($"[海床] 内置面板功能落盘失败（海床里缺该副本）{kv.Key}：{ex.Message}");
                }
            }
        }

        private static void SeedWidgets(string root)
        {
            foreach (var kv in WidgetMeta)
            {
                try
                {
                    string id = kv.Key;
                    // ★ 顺序很重要：**先取源码，再决定要不要建目录**。
                    //   以前是"先 CreateDirectory、后读源码、空就静默 continue"，于是发布版（没有源码树）
                    //   会在海床里留下 6 个**空目录** —— 比"根本没有这个目录"更误导用户。
                    // ★ 从项目 seabed/ 原样复制内置源码（与编译进 exe 的一致）；
                    //   "用户没改过的那份"会随源码升级，改过的一律不碰 —— 见 SyncTemplateFile
                    var (xaml, cs) = LoadWidgetFiles(id);
                    if (string.IsNullOrEmpty(xaml) && string.IsNullOrEmpty(cs))
                    {
                        // 这里以前是静默 continue：发布版缺 6 个小组件，日志里一个字都没有
                        LogManager.Warning($"[海床] 内置小组件 {id} 无可用源码，跳过落盘（海床里不会有该副本）");
                        continue;
                    }
                    // ★ 扁平化后：每个面板一个目录，全部同级（原先埋在 面板/小组件/ 下）
                    string dir = Path.Combine(root, "面板", Sanitize(id));
                    Directory.CreateDirectory(dir);
                    var hashes = ReadSeededHashes(dir);
                    if (!string.IsNullOrEmpty(xaml)) SyncTemplateFile(dir, id + ".xaml", xaml, hashes);
                    if (!string.IsNullOrEmpty(cs))
                        SyncTemplateFile(dir, string.IsNullOrEmpty(xaml) ? "main.cs" : id + ".xaml.cs", cs, hashes);
                    WriteManifest(dir, id, kv.Value.Name, "Widget", "小组件", "1.0.0", "widget-" + id, hashes);
                }
                catch (Exception ex)
                {
                    // 注意：id 是 try 块内的局部变量，catch 里看不到，只能用 kv.Key
                    LogManager.Warning($"[海床] 内置小组件落盘失败（海床里缺该副本）{kv.Key}：{ex.Message}");
                }
            }
        }

        /// <summary>内嵌小组件源码的资源名前缀。完整名由 csproj 的**通配**推导成
        /// <c>ShoreHue.Seabed.Widgets.&lt;id&gt;.&lt;原文件名&gt;</c>，所以下面是按前缀查找、按扩展名分流，
        /// 而不是去猜一个固定键。</summary>
        private const string EmbeddedWidgetPrefix = "ShoreHue.Seabed.Widgets.";

        /// <summary>
        /// 读取小组件的 XAML + cs 源码 —— **只从嵌入资源读**（一条路径，开发机与发布版完全一致）。
        ///
        /// 演变：最初只读源码目录 <c>seabed\小组件\&lt;id&gt;\</c>（靠 FindSourceRoot() 找 csproj），
        /// 于是用户机器上（没有源码树）读不到 → 6 个内置小组件落不了盘、海床里只剩空目录
        /// （2026-09 用官方 v1.1.0 exe 实测确认）。补嵌入资源时先写成了"源码目录 → 嵌入资源"两级，
        /// 但**嵌入资源在开发机上也存在**（csproj 无论在哪构建都会嵌进去），那一级纯属多余，
        /// 还又造出"两条路径、只有一条被日常使用"的形状 —— 已删除。
        /// </summary>
        private static (string Xaml, string Cs) LoadWidgetFiles(string id)
        {
            // 资源名由 csproj 的通配自动推导：ShoreHue.Seabed.Widgets.<id>.<原文件名>
            //   → 按前缀 + 扩展名找，不猜固定键；新增小组件也不用动 csproj。
            var asm = typeof(BuiltinTemplateSeeder).Assembly;
            string prefix = EmbeddedWidgetPrefix + id + ".";
            string x = "", c = "";
            foreach (string res in asm.GetManifestResourceNames())
            {
                if (!res.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (res.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)) c = ReadResource(asm, res);
                else if (res.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) x = ReadResource(asm, res);
                else if (res.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) c = ReadResource(asm, res);   // 纯 cs 小组件（web）
            }
            if (x.Length == 0 && c.Length == 0)
                LogManager.Warning($"[海床] 内置小组件 {id} 读不到源码（嵌入资源里缺了这一项？）——海床里不会出现该副本");
            return (x, c);
        }

        private static string ReadResource(System.Reflection.Assembly asm, string name)
        {
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) return "";
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        private static void WriteTemplateFile(string dir, string fileName, string content)
        {
            string path = Path.Combine(dir, fileName);
            if (File.Exists(path))
            {
                // ★ 内容一致 → 跳过（防止每次启动重写触发 FileSystemWatcher 死循环）
                string existing = File.ReadAllText(path);
                if (existing == content) return;
                // 用户改过（内容不同）→ 不覆盖（内置文件只在 Seeder 更新模板版本时由版本机制处理）
                return;
            }
            else
            {
                File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
            }
        }

        private static void WriteManifest(string dir, string id, string name, string kind, string category, string version,
                                          string sourceKey, SortedDictionary<string, string>? seededHashes = null)
        {
            string mf = Path.Combine(dir, "manifest.json");
            var manifest = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = name,
                ["kind"] = kind,
                ["category"] = category,
                ["version"] = version,
                ["author"] = "timecolors",
                ["sourceKey"] = sourceKey,
                ["system"] = true,                       // ★ 内置标记：删除时警告；升级时未改可更新
                ["templateVersion"] = TemplateVersion,
                ["permissions"] = new List<string>()
            };
            // ★ 落盘基线：每个模板文件写入时的内容哈希。下次启动据此判断"用户动过没有"——
            //   没动过才能随版本升级，动过就永不覆盖。SortedDictionary 保证序列化稳定（下面的"内容相同就不写"才可靠）。
            if (seededHashes != null) manifest["seededFiles"] = seededHashes;
            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            // 内容一致就不写：每次启动重写会让 mtime 抖动，白引发一轮 watcher 事件
            try { if (File.Exists(mf) && File.ReadAllText(mf) == json) return; }
            catch (Exception ex) { LogManager.Debug($"[海床] 读取旧 manifest 失败（照常重写）{mf}：{ex.Message}"); }
            File.WriteAllText(mf, json, new System.Text.UTF8Encoding(false));
        }

        /// <summary>文本内容哈希（SHA256 前 16 位十六进制；与插件信任哈希同一口径）。</summary>
        private static string HashOf(string text)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text ?? "")))[..16];
        }

        /// <summary>读取 manifest 里记录的落盘哈希（老版本没有 seededFiles → 空表 = "不知道用户动过没有"）。</summary>
        private static SortedDictionary<string, string> ReadSeededHashes(string dir)
        {
            var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string mf = Path.Combine(dir, "manifest.json");
                if (!File.Exists(mf)) return map;
                using var doc = JsonDocument.Parse(File.ReadAllText(mf));
                if (doc.RootElement.TryGetProperty("seededFiles", out var sf) && sf.ValueKind == JsonValueKind.Object)
                    foreach (var p in sf.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString() ?? "";
            }
            catch (Exception ex)
            {
                // 读不出记录 = 当"没有记录"（保守：不升级、不覆盖），不能当"一致"
                LogManager.Debug($"[海床] 读取落盘基线失败（按无记录处理，本次不升级）{dir}：{ex.Message}");
            }
            return map;
        }

        /// <summary>
        /// 落盘 / 升级**代码模板**文件（小组件、面板功能）。返回是否写了盘。
        ///   · 目标不存在 → 首次落盘；
        ///   · 有基线记录且磁盘哈希 == 基线 → 用户没动过：源码变了就升级覆盖，没变就跳过；
        ///   · 有基线记录但磁盘哈希 != 基线 → **用户改过，一个字节都不碰**；
        ///   · 没有基线记录（老版本 manifest）→ 只把当前内容登记为基线，本次不覆盖。
        /// ★ 只用于代码模板：配置节点（config.json 里装的是用户当前设置）永远不升级，见 SeedConfigNodes。
        /// </summary>
        internal static bool SyncTemplateFile(string dir, string fileName, string content, SortedDictionary<string, string> hashes)
        {
            string path = Path.Combine(dir, fileName);
            string srcHash = HashOf(content);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
                hashes[fileName] = srcHash;
                return true;
            }
            string curHash = HashOf(File.ReadAllText(path));
            if (!hashes.TryGetValue(fileName, out var seeded))
            {
                hashes[fileName] = curHash;      // 没有记录：只建立基线，绝不覆盖
                return false;
            }
            if (curHash != seeded) return false; // 用户改过 → 不碰
            if (curHash == srcHash) return false;// 已经一致
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
            hashes[fileName] = srcHash;
            LogManager.Info($"[海床] 内置模板已升级：{Path.GetFileName(dir)}/{fileName}（用户没改过 → 随版本更新）");
            return true;
        }

        private static string Sanitize(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s) if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            return sb.ToString();
        }
    }
}
