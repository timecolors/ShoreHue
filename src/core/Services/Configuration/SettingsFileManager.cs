using System;
using System.IO;
using System.Text.Json;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Infrastructure.Utils;
using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Configuration;
namespace ShoreHue.Core.Services
{
    /// <summary>
    /// 配置文件读写专用。
    ///
    /// ★ 路径**每次调用按当前 `AppPaths.DataRoot` 解析，绝不缓存成 static readonly**：
    ///   该值会随 `TestDataRoot` 变化（单元测试用它隔离数据目录），一旦在"类首次被触碰"时冻住路径，
    ///   之后所有读写都会落到同一个文件上——测试就会写进**用户真实的 config.json**。
    ///   需要"锁定某个具体配置文件"的调用方（如 `SettingsManager` 的防抖落盘）请显式传路径进来。
    /// </summary>
    public static class SettingsFileManager
    {
        /// <summary>
        /// 从文件加载配置；`path` 为空时用当前数据根的 config.json。
        /// </summary>
        public static SettingsData Load(string? path = null)
        {
            string cfg = path ?? AppPaths.ConfigPath;
            if (!File.Exists(cfg))
                return new SettingsData();

            try
            {
                string json = File.ReadAllText(cfg);
                var data = JsonSerializer.Deserialize<SettingsData>(json);
                data ??= new SettingsData();
                // ★ 旧版"呼出/隐藏共用"→ 新版"触发/隐藏分设"动画迁移（仅一次），迁移后落盘保留
                if (MigrateAnimationSettings(data))
                {
                    Save(data, cfg);
                }
                return data;
            }
            catch (Exception ex)
            {
                // ★ 文件损坏时必须备份原文件，不能静默返回空配置——
                //   否则下一次 Save（防抖落盘/恢复/应用预设）会把空配置写盘，用户设置永久丢失
                try
                {
                    string backup = cfg + ".bak";
                    File.Copy(cfg, backup, true);
                    LogManager.Error("config.json 解析失败，已备份为 " + backup + "：" + ex.Message);
                }
                catch (Exception bex)
                {
                    // ★ 数据安全底线：配置损坏 + 连备份都失败 = 用户设置可能永久丢失，必须 Error
                    LogManager.Error($"config.json 解析失败，且备份到 {cfg}.bak 也失败（配置可能丢失）：{bex.Message}", bex);
                }
                return new SettingsData();
            }
        }

        /// <summary>旧 ShowHide 设置 → 新 Show/Hide 动画（保留用户已有 ElasticEase/时长）。</summary>
        private static bool MigrateAnimationSettings(SettingsData d)
        {
            bool changed = false;
            try
            {
                if (string.IsNullOrEmpty(d.ShowAnimationType) || d.ShowAnimationDurationMs <= 0)
                {
                    d.ShowAnimationType = d.ShowHideEasingType switch
                    {
                        "ElasticEase" => "Elastic",
                        "BackEase" => "Elastic",
                        _ => "Slide"
                    };
                    d.ShowAnimationDurationMs = d.ShowHideDurationMs;
                    changed = true;
                }
                if (string.IsNullOrEmpty(d.HideAnimationType) || d.HideAnimationDurationMs <= 0)
                {
                    d.HideAnimationType = d.ShowAnimationType;
                    d.HideAnimationDurationMs = d.ShowAnimationDurationMs;
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                // 旧版"呼出/隐藏共用"动画设置没迁过来 → 动画会退回默认（功能降级，不丢数据）
                LogManager.Warning($"旧版动画设置迁移失败（动画将用默认值）：{ex.Message}");
            }
            return changed;
        }

        /// <summary>
        /// 保存配置到文件（★ 原子写：临时文件 + File.Replace，崩溃/断电不会留下半写损坏的 config.json）。
        /// `path` 为空时用当前数据根的 config.json；需要锁定目标文件的调用方（防抖落盘）应显式传入。
        /// 返回是否真的落盘成功——调用方（`SettingsManager`）靠它决定"脏标记"能否清掉，
        /// 失败时保持脏，等下一次防抖/关闭再重试，而不是把内存快照当成已持久化。
        /// </summary>
        public static bool Save(SettingsData data, string? path = null)
        {
            string cfg = path ?? AppPaths.ConfigPath;
            try
            {
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                string tmp = cfg + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(cfg))
                {
                    File.Replace(tmp, cfg, null, true);
                }
                else
                {
                    File.Move(tmp, cfg);
                }
                return true;
            }
            catch (Exception ex)
            {
                // ★★ 最要命的静默失败：用户改了设置却没落盘（下次启动全没了）——必须 Error。
                //    原子写：先写 .tmp 再替换，失败时删掉 .tmp，旧 config.json 保持不变（数据不半截）。
                LogManager.Error($"保存 config.json 失败（本次设置改动未落盘）：{ex.Message}", ex);
                try { if (File.Exists(cfg + ".tmp")) File.Delete(cfg + ".tmp"); }
                catch (Exception dex) { LogManager.Debug($"[设置] 清理临时文件失败（无害）{cfg}.tmp：{dex.Message}"); }
                return false;
            }
        }
    }
}
