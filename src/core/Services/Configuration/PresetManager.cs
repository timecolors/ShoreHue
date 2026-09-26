using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShoreHue.Core.Infrastructure.Logging;

namespace ShoreHue.Core.Services.Configuration
{
    /// <summary>
    /// 预设管理（海床编程模式）：把配置保存为命名预设。
    /// - 整套预设：SettingsData 全量快照；
    /// - 局部预设：只保存指定字段子集（如单个面板/功能的配置）；
    /// 应用预设 = 反序列化 → 写回配置 → SettingsManager.Reload 全量生效。
    /// 预设存储于 %LOCALAPPDATA%\ShoreHue\Presets\&lt;名称&gt;.json。
    /// </summary>
    public static class PresetManager
    {
        /// <summary>测试注入的预设目录（单测用临时目录，避免污染真实用户数据）。</summary>
        internal static string? TestPresetsDir;

        public static string PresetsDir => TestPresetsDir ?? AppPaths.PresetsDir;

        private static string FileFor(string name) => Path.Combine(PresetsDir, Sanitize(name) + ".json");

        private static string Sanitize(string name)
        {
            // ★ 非法字符替换成 `_` 而不是删掉：删除会让 `a/b` 与 `ab` 撞到同一个文件名，
            //   后保存的那个**静默覆盖**前一个（两个不同预设看起来都还在列表里，其实只剩一个）。
            var invalid = Path.GetInvalidFileNameChars();
            string s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return string.IsNullOrEmpty(s) ? "未命名" : s;
        }

        /// <summary>
        /// 原子写预设文件（临时文件 + File.Replace）。
        /// 直接用 File.WriteAllText 的话，写到一半崩溃/断电会留下**截断的 JSON**，
        /// 而 LoadPreset 只会报"读取预设失败" → 用户的预设就这么没了。
        /// </summary>
        private static bool WritePresetFile(string path, string json)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Replace(tmp, path, null, true);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception ex)
            {
                LogManager.Error($"[预设] 写入预设文件失败（{Path.GetFileName(path)}）：{ex.Message}", ex);
                try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); }
                catch { /* 清理失败无害：下次写入会覆盖同名临时文件 */ }
                return false;
            }
        }

        /// <summary>列出所有预设名（按修改时间倒序）。</summary>
        public static List<string> ListPresets()
        {
            try
            {
                if (!Directory.Exists(PresetsDir)) return new List<string>();
                return Directory.GetFiles(PresetsDir, "*.json")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .OrderByDescending(n => File.GetLastWriteTime(Path.Combine(PresetsDir, n + ".json")))
                    .ToList()!;
            }
            catch (Exception ex)
            {
                // 返回空列表 = 界面显示"没有预设"（用户会以为预设全丢了），必须留痕
                LogManager.Warning($"[预设] 列出预设失败（界面将显示为空）：{ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>保存整套预设（SettingsData 全量）。</summary>
        public static void SaveFull(string name, SettingsData data)
        {
            Directory.CreateDirectory(PresetsDir);
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            WritePresetFile(FileFor(name), json);
        }

        /// <summary>
        /// 局部/整套的显式标记键。
        /// ★ 为什么需要它：旧实现用"字段数 &lt; 20"来猜是局部还是整套预设 —— 而局部预设
        /// 是**字段子集**，一个稍微大一点的功能（比如某个区域的尺寸/面板节点）就能带 20+ 个字段，
        /// 于是被当成**整套**应用：`SettingsFileManager.Save(data)` 直接把配置替换成
        /// "只有这些字段的 SettingsData"，其余设置全部回落默认值（静默重置用户配置）。
        /// 现在保存时写入显式标记；读取时标记优先，仅对**没有**标记的旧文件保留字段数回退。
        /// </summary>
        internal const string PartialMarkerKey = "__partial";

        /// <summary>保存局部预设（只含指定字段子集，可局部替换）。</summary>
        public static void SavePartial(string name, SettingsData data, IEnumerable<string> fields)
        {
            Directory.CreateDirectory(PresetsDir);
            var full = JsonNode.Parse(JsonSerializer.Serialize(data))!.AsObject();
            var subset = new JsonObject { [PartialMarkerKey] = true };   // ★ 显式声明"这是局部预设"
            foreach (var f in fields)
            {
                if (full.TryGetPropertyValue(f, out var v) && v != null)
                {
                    subset[f] = v.DeepClone();
                }
            }
            WritePresetFile(FileFor(name),
                subset.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>读取预设（整套：完整 SettingsData；局部：仅字段子集的 SettingsData）。</summary>
        public static SettingsData? LoadPreset(string name)
        {
            try
            {
                string file = FileFor(name);
                if (!File.Exists(file)) return null;
                string json = File.ReadAllText(file);
                return JsonSerializer.Deserialize<SettingsData>(json);
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[预设] 读取预设失败（{name}）：{ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 应用预设到当前配置（写盘 + SettingsManager.Reload 生效）。
        /// 局部预设（字段子集，小预设）合并回当前配置；整套预设（大预设）整体替换。
        /// </summary>
        public static bool ApplyPreset(string name, ISettingsService settings)
        {
            try
            {
                string file = FileFor(name);
                if (!File.Exists(file)) return false;
                string json = File.ReadAllText(file);
                var presetObj = JsonNode.Parse(json)?.AsObject();
                if (presetObj == null) return false;

                var data = JsonSerializer.Deserialize<SettingsData>(json);
                if (data == null) return false;

                // ★ 局部 / 整套的判定：**显式标记优先**，旧的"字段数 < 20"只作为无标记文件的回退。
                //   （原因见 PartialMarkerKey 注释：按字段数猜会把稍大的局部预设当成整套 → 静默重置其余设置。）
                bool isPartial = presetObj.TryGetPropertyValue(PartialMarkerKey, out var pmNode) && pmNode != null
                    ? pmNode.GetValue<bool>()
                    : presetObj.Count < 20;
                if (isPartial)
                {
                    var current = SettingsFileManager.Load();
                    var curObj = JsonNode.Parse(JsonSerializer.Serialize(current))!.AsObject();
                    foreach (var (k, v) in presetObj)
                    {
                        if (v != null) curObj[k] = v.DeepClone();
                    }
                    data = JsonSerializer.Deserialize<SettingsData>(curObj.ToJsonString());
                    if (data == null) return false;
                }

                // ★ 安全 v2 防洗白：预设文件是**数据**，不能替代码授信。
                //   预设里"新引入"的自定义面板（当前配置中不存在的 id）一律降级为不可信 ——
                //   否则一个恶意预设只要把 CustomPanels 写成 TrustedSource:true 就绕过了沙箱。
                //   已存在面板保持原有信任状态（本地自写的东西不会被预设改坏）。
                try
                {
                    var current = SettingsFileManager.Load();
                    var known = new HashSet<string>((current.CustomPanels ?? new List<ShoreHue.Core.Models.CustomPanelDefinition>())
                        .Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
                    if (data.CustomPanels != null)
                    {
                        foreach (var cp in data.CustomPanels)
                        {
                            if (cp == null || known.Contains(cp.Id)) continue;
                            cp.TrustedSource = false;
                        }
                    }

                    // ★ 同一件事的另一半：信任表本身同样不能由预设注入。
                    //   只对 CustomPanels 降级是不够的 —— 一条 TrustedPlugins 记录就能让任意
                    //   外来包在下次加载时直接跳过整个沙箱（ComputeTrust 第一步就判 trustedByHash）。
                    PluginTrustSanitizer.StripExternalTrust(data, current.TrustedPlugins, $"预设「{name}」");
                }
                catch (Exception ex)
                {
                    // ★★ fail-closed：防洗白检查本身失败就**不能继续应用** ——
                    //    否则预设文件里写 TrustedSource:true 的新面板会直接带着"受信"落盘（免检牌）。
                    LogManager.Error($"[预设] 防洗白检查失败，已拒绝应用预设「{name}」：{ex.Message}", ex);
                    return false;
                }

                SettingsFileManager.Save(data);
                settings.Host().Reload();
                return true;
            }
            catch (Exception ex)
            {
                LogManager.Error($"[预设] 应用预设失败（{name}）：{ex.Message}", ex);
                return false;
            }
        }

        /// <summary>返回预设文件覆盖的字段名列表（整套预设返回全部 SettingsData 字段，局部返回子集字段）。</summary>
        public static System.Collections.Generic.List<string> AppliedFields(string name)
        {
            var result = new System.Collections.Generic.List<string>();
            try
            {
                string file = FileFor(name);
                if (!File.Exists(file)) return result;
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(file));
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    // 跳过内部标记键：它不是设置字段，混进"覆盖字段列表"会让冲突标记/变灰逻辑错位
                    if (p.Name == PartialMarkerKey) continue;
                    result.Add(p.Name);
                }
            }
            catch (Exception ex)
            {
                // 返回空 = 界面不标"该预设覆盖了哪些设置"（标记缺失，功能降级）
                LogManager.Warning($"[预设] 解析预设覆盖字段失败（{name}）：{ex.Message}");
            }
            return result;
        }

        public static bool DeletePreset(string name)
        {
            try
            {
                string file = FileFor(name);
                if (File.Exists(file)) { File.Delete(file); return true; }
            }
            catch (Exception ex)
            {
                LogManager.Error($"[预设] 删除预设失败（{name}）：{ex.Message}", ex);
            }
            return false;
        }
    }
}
