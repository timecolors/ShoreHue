using System;
using ShoreHue.Core.Infrastructure.Logging;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShoreHue.Core.Services.Ai
{
    /// <summary>
    /// OpenAI 兼容的 Chat Completions 客户端：
    ///  - 流式（SSE）对话，逐字回调
    ///  - 非流式测试连接
    /// 兼容 DeepSeek / OpenAI / SiliconFlow / Ollama / OpenRouter / Moonshot / 智谱 / Groq 等。
    /// </summary>
    public class AiChatClient : IDisposable
    {
        private readonly HttpClient _http;

        public AiChatClient()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("ShoreHue/1.1");
        }

        /// <summary>测试连接：发送最小请求，成功返回 null，失败返回错误描述。</summary>
        public async Task<string?> TestConnectionAsync(AiSettings settings, CancellationToken ct = default)
        {
            try
            {
                var body = new
                {
                    model = settings.Model,
                    messages = new[] { new { role = "user", content = "ping" } },
                    max_tokens = 1,
                    stream = false
                };
                using var req = BuildRequest(settings, body);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
                string text = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    return $"HTTP {(int)resp.StatusCode}: {Truncate(text, 200)}";
                }
                return null;
            }
            catch (OperationCanceledException)
            {
                return ShoreHue.UI.Localization.LocalizationManager.Instance["Ai_Timeout"];
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// 流式对话：把 userText 追加到 history 发送，收到的增量通过 onDelta 回调（非 UI 线程）。
        /// 返回完整回复文本；失败抛出异常（由调用方提示）。
        /// </summary>
        public async Task<string> StreamChatAsync(
            AiSettings settings,
            List<ChatMessage> history,
            string userText,
            Action<string> onDelta,
            CancellationToken ct = default,
            ChatMessage? lastUser = null)
        {
            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
            {
                messages.Add(new { role = "system", content = settings.SystemPrompt });
            }
            foreach (var m in history)
            {
                messages.Add(new { role = m.RoleName, content = BuildContent(m.Content, m) });
            }
            // 最后一条用户消息：若带图片则整体作为 image_url 内容发送
            messages.Add(lastUser != null
                ? new { role = "user", content = BuildContent(lastUser.Content, lastUser) }
                : new { role = "user", content = BuildContent(userText, null) });

            var body = new
            {
                model = settings.Model,
                messages,
                temperature = settings.Temperature,
                stream = true,
                // ★ 深度思考：OpenAI 兼容的 reasoning_effort（需服务商支持）
                reasoning_effort = settings.EnableReasoning ? "high" : null,
                // ★ 联网搜索：DeepSeek 等支持的 web_search 工具
                tools = settings.EnableWebSearch
                    ? new object[] { new { type = "web_search" } }
                    : null
            };

            using var req = BuildRequest(settings, body);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                string err = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"请求失败 HTTP {(int)resp.StatusCode}: {Truncate(err, 300)}");
            }

            var sb = new StringBuilder();
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                ct.ThrowIfCancellationRequested();
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                string payload = line["data:".Length..].Trim();
                if (payload == "[DONE]") break;

                string? delta = TryParseDelta(payload);
                if (!string.IsNullOrEmpty(delta))
                {
                    sb.Append(delta);
                    onDelta?.Invoke(delta);
                }
                else if (TryParseStreamError(payload, out string streamErr))
                {
                    // ★ 流中错误必须抛出来：OpenAI 兼容服务商在中途报错时发的是
                    //   {"error":{...}} 或 choices 里带 finish_reason/error 的负载，**没有** delta.content。
                    //   以前这类负载被 TryParseDelta 当成"没这个字段"静默忽略 →
                    //   调用方拿到空串，把**一条空回复**当成正常回复渲染并写进会话历史，
                    //   用户看到的是"AI 回了个空白"，完全不知道出了错。
                    throw new InvalidOperationException("模型返回错误：" + streamErr);
                }
            }
            // 一个增量都没有：同样不能当成"正常但内容为空"，否则会污染会话历史
            if (sb.Length == 0)
                throw new InvalidOperationException("模型没有返回任何内容（服务商可能拒绝了该请求或响应格式不兼容）");
            return sb.ToString();
        }

        /// <summary>从流式负载里提取错误信息（{"error": …} 或 choices[0].error）。无错误返回 false。</summary>
        private static bool TryParseStreamError(string payload, out string message)
        {
            message = "";
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err))
                {
                    message = ExtractMessage(err);
                    return message.Length > 0;
                }
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array &&
                    choices.GetArrayLength() > 0 && choices[0].TryGetProperty("error", out var cerr))
                {
                    message = ExtractMessage(cerr);
                    return message.Length > 0;
                }
            }
            catch (Exception ex)
            {
                // 解析失败 → 交由调用方按"没有错误"处理（会话正文仍会走空回复保护）
                LogManager.Debug($"[AI] 解析流式错误负载失败（按无错误处理）：{ex.Message}");
            }
            return false;
        }

        private static string ExtractMessage(JsonElement err)
        {
            if (err.ValueKind == JsonValueKind.String) return err.GetString() ?? "";
            if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m))
                return m.GetString() ?? "";
            return err.ToString();
        }

        /// <summary>
        /// 用模型生成简洁会话标题（第一条用户消息的总结）。失败返回 null，由调用方回退截断。
        /// </summary>
        public async Task<string?> GenerateTitleAsync(AiSettings settings, string firstUserText, CancellationToken ct = default)
        {
            try
            {
                var body = new
                {
                    model = settings.Model,
                    messages = new object[]
                    {
                        new { role = "user", content =
                            "为下面这个问题生成一个不超过 12 个字的标题，直接返回标题本身，不要引号、标点或多余文字：\n" + firstUserText }
                    },
                    max_tokens = 24,
                    temperature = 0.3,
                    stream = false
                };
                using var req = BuildRequest(settings, body);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
                if (!resp.IsSuccessStatusCode) return null;
                string json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    string t = content.GetString()?.Trim().Trim('"', '“', '”', '「', '」', '。', '.', ' ', '\n') ?? "";
                    return t.Length > 0 ? t : null;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 构造 OpenAI 兼容的 content：纯文本为字符串；带图片时为
        /// [{ type: text }, { type: image_url, image_url: { url: dataURI } }] 数组。
        /// </summary>
        private static object BuildContent(string text, ChatMessage? msg)
        {
            if (msg == null || !msg.HasImage)
                return text;

            var parts = new List<object>();
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(new { type = "text", text });
            parts.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:{msg.ImageMime ?? "image/png"};base64,{msg.ImageBase64}" }
            });
            return parts;
        }

        private HttpRequestMessage BuildRequest(AiSettings settings, object body)
        {
            string url = settings.BaseUrl.TrimEnd('/');
            if (!url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                url += "/chat/completions";

            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.ApiKey.Trim());
            }
            return req;
        }

        /// <summary>粗略估算 token 数（中文/英文混合约 2-4 字符/token，取 3 保守）。</summary>
        public static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return Math.Max(1, (int)Math.Ceiling(text.Length / 3.0));
        }

        /// <summary>估算一段消息列表的总 token（含角色开销）。</summary>
        public static int EstimateMessagesTokens(IEnumerable<ChatMessage> messages)
        {
            int total = 0;
            foreach (var m in messages)
            {
                total += EstimateTokens(m.Content) + 4; // 角色/结构开销
                if (m.HasImage) total += 800; // 图片按固定开销估算
            }
            return total;
        }

        internal static string? TryParseDelta(string payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var choices) &&
                    choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.String)
                    {
                        string? value = content.GetString();
                        return string.IsNullOrEmpty(value) ? null : value;
                    }
                }
            }
            catch (Exception ex)
            {
                // 尽力而为：某个响应字段解析不出来就返回 null，调用方按"没有该字段"处理
                LogManager.Debug($"[AI] 解析响应字段失败（按缺失处理）：{ex.Message}");
            }
            return null;
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "...";

        public void Dispose() => _http.Dispose();
    }
}
