using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Infrastructure.Utils;

namespace ShoreHue.Core.Services.Ai
{
    public sealed class AiSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// 会话标题。默认值是**未命名哨兵**（见 <see cref="UntitledTitle"/>）。
        /// ★ 不能用本地化字符串做哨兵做比较：`AiChatView` 以前拿 `Title == LocalizationManager["Ai_NewChatTitle"]`
        ///   判断"这是新会话"，而这里的默认值是**硬编码中文**"新对话" ——
        ///   英文界面下两者永不相等，于是自动命名（模型生成标题 / 用首条消息命名）永远不触发，
        ///   用户在英文界面里会一直看到中文的"新对话"。
        /// </summary>
        public string Title { get; set; } = UntitledTitle;

        /// <summary>未命名标题的稳定哨兵（**不随语言变化**，供比较用；界面显示时再本地化）。</summary>
        public const string UntitledTitle = "新对话";
        public static readonly string[] UntitledTitles =
            { UntitledTitle, "New chat", "New Chat" };   // 兼容历史数据与本地化取值

        /// <summary>
        /// 标题是否仍是"未命名"（跨语言判定）。
        /// ★ 除了历史字面量，还比对**本地化字典里的 §Session_New**（"新对话"/"New chat"）：
        ///   那条键本来就是给这个用途准备的，却一直没人用 —— 于是"未命名标题"的判定只能靠
        ///   代码里硬编码的语言字面量。接上它以后，新增语言只需改字典，不必改这里。
        /// </summary>
        public static bool IsUntitled(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return true;
            if (UntitledTitles.Contains(title)) return true;
            try
            {
                return string.Equals(title,
                    ShoreHue.UI.Localization.LocalizationManager.Instance["Session_New"],
                    StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                // 取不到本地化（极端环境）→ 只靠字面量判断（有确定性回退）
                Core.Infrastructure.Logging.LogManager.Debug($"[AI] 读取 Session_New 失败（按字面量判断）：{ex.Message}");
                return false;
            }
        }

        public DateTime Created { get; set; } = DateTime.Now;
        public List<ChatMessage> Messages { get; set; } = new();
    }

    public sealed class AiSessionData
    {
        public string CurrentId { get; set; } = "";
        public List<AiSession> Sessions { get; set; } = new();
    }

    /// <summary>
    /// AI 多会话存储（ai_sessions.json）与旧版单会话历史（ai_history.json）的迁移。
    /// </summary>
    public static class AiSessionStore
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static AiSessionData Load()
        {
            try
            {
                if (File.Exists(AppPaths.AiSessionsPath))
                {
                    var data = JsonSerializer.Deserialize<AiSessionData>(File.ReadAllText(AppPaths.AiSessionsPath));
                    if (data != null && data.Sessions.Count > 0) return data;
                }
            }
            catch (Exception ex)
            {
                // 读不出来就往下走"迁移旧版单会话"这条路（有降级，故 Debug）
                LogManager.Debug($"[AI] 读取会话失败（尝试迁移旧历史）：{ex.Message}");
            }

            // 迁移旧版单会话历史
            try
            {
                if (File.Exists(AppPaths.AiHistoryPath))
                {
                    var old = JsonSerializer.Deserialize<List<ChatMessage>>(File.ReadAllText(AppPaths.AiHistoryPath));
                    if (old != null && old.Count > 0)
                    {
                        var session = new AiSession
                        {
                            Title = BuildTitle(old),
                            Messages = old
                        };
                        var data = new AiSessionData { CurrentId = session.Id, Sessions = { session } };
                        Save(data);
                        return data;
                    }
                }
            }
            catch (Exception ex)
            {
                // 旧版单会话历史迁不过来 → 本次以空会话开始（旧记录文件仍在，不会删）
                LogManager.Warning($"[AI] 迁移旧版对话历史失败（本次以空会话开始）：{ex.Message}");
            }

            var fresh = new AiSessionData();
            var first = new AiSession();
            fresh.Sessions.Add(first);
            fresh.CurrentId = first.Id;
            return fresh;
        }

        public static void Save(AiSessionData data)
        {
            try
            {
                // 对话历史完整保留：每会话仅设极端兜底（5 万条 ≈ 数十 MB），正常使用永不触发。
                // 发送给模型时的上下文裁剪在 AiChatView 发送阶段进行，与存储无关。
                foreach (var s in data.Sessions)
                {
                    if (s.Messages.Count > 50000)
                        s.Messages.RemoveRange(0, s.Messages.Count - 50000);
                }
                // 会话数量上限放宽到 100（每个会话容量小，可自行删除）
                if (data.Sessions.Count > 100)
                    data.Sessions.RemoveRange(0, data.Sessions.Count - 100);

                string? dir = Path.GetDirectoryName(AppPaths.AiSessionsPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(AppPaths.AiSessionsPath, JsonSerializer.Serialize(data, Options));
            }
            catch (Exception ex)
            {
                // 会话没落盘 = 重启后这次的对话记录丢失
                LogManager.Warning($"[AI] 保存会话失败（重启后对话记录丢失）：{ex.Message}");
            }
        }

        private static string BuildTitle(List<ChatMessage> messages)
        {
            var firstUser = messages.FirstOrDefault(m => m.Role == ChatRole.User);
            if (firstUser == null) return ShoreHue.UI.Localization.LocalizationManager.Instance["Session_Old"];
            string t = firstUser.Content.Trim();
            return t.Length > 20 ? t[..20] + "…" : t;
        }
    }
}