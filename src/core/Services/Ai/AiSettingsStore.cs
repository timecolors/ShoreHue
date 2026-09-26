using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Infrastructure.Utils;

namespace ShoreHue.Core.Services.Ai
{
    /// <summary>
    /// AI 配置与对话历史存储（本地 JSON，无任何上传）。
    /// </summary>
    public static class AiSettingsStore
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static AiSettings Load()
        {
            try
            {
                if (File.Exists(AppPaths.AiSettingsPath))
                {
                    string json = File.ReadAllText(AppPaths.AiSettingsPath);
                    var data = JsonSerializer.Deserialize<AiSettings>(json);
                    if (data != null)
                    {
                        // ★ 解密 ApiKey：优先用 DPAPI 加密字段；兼容旧版明文 ApiKey（迁移后下次保存会加密）
                        data.ApiKey = DecryptKey(data.ApiKeyEncrypted);
                        if (string.IsNullOrEmpty(data.ApiKey) && !string.IsNullOrEmpty(data.ApiKeyLegacy))
                        {
                            data.ApiKey = data.ApiKeyLegacy;
                            data.ApiKeyEncrypted = "";
                        }
                        return data;
                    }
                }
            }
            catch (Exception ex)
            {
                // 读不出 AI 配置 → 界面显示"未配置"（用户以为配置丢了），必须留痕
                LogManager.Warning($"[AI] 读取 AI 配置失败（按未配置处理）：{ex.Message}");
            }
            return new AiSettings();
        }

        /// <summary>保存 AI 配置。返回错误信息（空串 = 成功）—— 调用方据此在界面上如实提示，
        /// 不要把"没保存成功"当成保存成功（密钥/服务商配置是用户手输的东西，静默丢失最伤）。</summary>
        public static string Save(AiSettings settings)
        {
            try
            {
                // ★ 安全：ApiKey 用 DPAPI（当前用户）加密后落盘，防止同机其他进程/用户读取明文
                string enc = EncryptKey(settings.ApiKey);
                if (!string.IsNullOrEmpty(settings.ApiKey) && enc.Length == 0)
                {
                    // ★★ 加密失败就**不写盘**：否则会用空串覆盖磁盘上原有的密文，把用户已保存的密钥悄悄毁掉
                    LogManager.Error("[AI] 密钥加密失败，已放弃本次保存（磁盘上的原密钥保持不变）");
                    return ShoreHue.UI.Localization.LocalizationManager.Instance["Ai_KeyEncryptFailed"];
                }
                settings.ApiKeyEncrypted = enc;
                settings.ApiKeyLegacy = "";   // 迁移后清掉旧明文
                string? dir = Path.GetDirectoryName(AppPaths.AiSettingsPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(AppPaths.AiSettingsPath, JsonSerializer.Serialize(settings, Options));
                return "";
            }
            catch (Exception ex)
            {
                LogManager.Error($"[AI] 保存 AI 配置失败：{ex.Message}", ex);
                return ShoreHue.UI.Localization.LocalizationManager.Instance["Ai_SaveFailed"] + ex.Message;
            }
        }

        private static string EncryptKey(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(plain);
                return Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
                    bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            }
            catch (Exception ex)
            {
                // 返回空串表示"加密失败"，由 Save 决定不写盘（绝不静默丢密钥）
                LogManager.Error($"[AI] DPAPI 加密密钥失败：{ex.Message}", ex);
                return "";
            }
        }

        private static string DecryptKey(string enc)
        {
            if (string.IsNullOrEmpty(enc)) return "";
            try
            {
                var bytes = Convert.FromBase64String(enc);
                return System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
                    bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            }
            catch (Exception ex)
            {
                // 解不出来（换了 Windows 账户 / 文件被改）→ 按"没配密钥"处理，界面会提示未配置
                LogManager.Warning($"[AI] DPAPI 解密密钥失败（按未配置密钥处理）：{ex.Message}");
                return "";
            }
        }

        // ========== 对话历史（最近一轮） ==========

        public static List<ChatMessage> LoadHistory()
        {
            try
            {
                if (File.Exists(AppPaths.AiHistoryPath))
                {
                    string json = File.ReadAllText(AppPaths.AiHistoryPath);
                    var list = JsonSerializer.Deserialize<List<ChatMessage>>(json);
                    if (list != null) return list;
                }
            }
            catch (Exception ex)
            {
                // 对话历史读不出来 → 从空历史开始（不影响新对话）
                LogManager.Debug($"[AI] 读取对话历史失败（从空历史开始）：{ex.Message}");
            }
            return new List<ChatMessage>();
        }

        public static void SaveHistory(List<ChatMessage> messages)
        {
            try
            {
                // 只保留最近 40 条，防止文件无限增长
                if (messages.Count > 40)
                    messages.RemoveRange(0, messages.Count - 40);
                string? dir = Path.GetDirectoryName(AppPaths.AiHistoryPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(AppPaths.AiHistoryPath, JsonSerializer.Serialize(messages, Options));
            }
            catch (Exception ex)
            {
                // 历史没落盘 = 重启后对话记忆丢失
                LogManager.Warning($"[AI] 保存对话历史失败（重启后记忆丢失）：{ex.Message}");
            }
        }
    }
}
