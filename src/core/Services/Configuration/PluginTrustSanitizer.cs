using System;
using System.Collections.Generic;
using System.Linq;
using ShoreHue.Core.Infrastructure.Logging;

namespace ShoreHue.Core.Services.Configuration
{
    /// <summary>
    /// 信任表（`SettingsData.TrustedPlugins`）的**外部输入净化**。
    ///
    /// 为什么必须有这一层：信任表存在 `config.json` 里，而它同时是"这个外来包免检"的唯一凭据。
    /// 只要有任何一条"外部数据 → SettingsData → 落盘"的通路没有清理它，
    /// 一条 `TrustedPlugins: { "victim": "<hash>" }` 就能让受害机上某个外来包**永久免检**
    /// ——这正是 `docs/SECURITY.md` 承诺"预设文件不能替代码授信"要防的事。
    ///
    /// 已知的三条外部输入通路（全部必须调用这里）：
    ///   1. 预设文件（整套 / 局部）→ `PresetManager.ApplyPreset`
    ///   2. 云端配置同步 → `ConfigSyncWindow.DownloadAsync`
    ///   3. `.shpkg` / 旧 `.dbp` 包（Kind=Full）→ `SeabedPackage` → `PresetManager`
    ///
    /// 策略：**只保留本机既有的、id 与哈希都完全一致的记录**（求交），其余一律丢弃。
    /// 求交而不是清空，是因为本机自己导出的整套预设本来就带着自己的信任记录，
    /// 清空会让"恢复自己的备份"变成"所有插件重新退回沙箱"，属于误伤。
    /// </summary>
    public static class PluginTrustSanitizer
    {
        /// <summary>
        /// 丢弃 `data` 中来自外部数据源的信任记录（与本机现有信任表求交）。
        /// 返回被丢弃的条数（0 = 无需净化）。
        /// </summary>
        /// <param name="data">待净化的配置（来自外部数据源）。</param>
        /// <param name="localTrust">本机当前的信任表；null / 空 = 本机没有任何信任记录，全部丢弃。</param>
        /// <param name="source">日志里标识来源（如"预设「我的预设」"/"云端配置"），仅用于留痕。</param>
        public static int StripExternalTrust(SettingsData data, IReadOnlyDictionary<string, string>? localTrust, string source)
        {
            if (data.TrustedPlugins == null || data.TrustedPlugins.Count == 0) return 0;

            int dropped = 0;
            try
            {
                if (localTrust == null || localTrust.Count == 0)
                {
                    dropped = data.TrustedPlugins.Count;
                    data.TrustedPlugins = null;
                }
                else
                {
                    foreach (var key in data.TrustedPlugins.Keys.ToList())
                    {
                        // 键按已有的 OrdinalIgnoreCase 语义查找（插件 id 来自目录名，大小写不敏感）；
                        // 哈希值必须逐字符相同 —— 内容一变信任即失效，这条不能被外部数据绕过。
                        if (localTrust.TryGetValue(key, out var localHash) &&
                            string.Equals(localHash, data.TrustedPlugins[key], StringComparison.Ordinal))
                        {
                            continue;
                        }
                        data.TrustedPlugins.Remove(key);
                        dropped++;
                    }
                    if (data.TrustedPlugins.Count == 0) data.TrustedPlugins = null;
                }
            }
            catch (Exception ex)
            {
                // ★ fail-closed：净化本身出错时宁可整表丢弃，也不能把可能被注入的信任放行
                data.TrustedPlugins = null;
                LogManager.Error($"[信任表] 净化来自{source}的信任记录失败，已整表丢弃：{ex.Message}", ex);
                return -1;
            }

            if (dropped > 0)
                LogManager.Warning($"[信任表] 已丢弃来自{source}的 {dropped} 条信任记录（外部数据不能替代码授信）");
            return dropped;
        }

        /// <summary>读取本机当前配置里的信任表（净化时的比对基准）。</summary>
        public static IReadOnlyDictionary<string, string>? ReadLocalTrust()
        {
            try
            {
                return SettingsFileManager.Load().TrustedPlugins;
            }
            catch (Exception ex)
            {
                // 读不到本机信任表 → 返回 null，调用方按"本机无信任记录"处理（全部丢弃，fail-closed）
                LogManager.Warning($"[信任表] 读取本机信任表失败（将按「本机无信任记录」处理）：{ex.Message}");
                return null;
            }
        }
    }
}
