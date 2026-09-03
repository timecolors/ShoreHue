using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShoreHue.UI.Seabed
{
    /// <summary>
    /// GitHub OAuth 设备流登录 + 市场内容管理（登录后删除自己上传的包）。
    /// 设备流：客户端获取验证码 → 用户在浏览器打开 github.com/login/device 授权 → 轮询换取 token。
    /// token 用 DPAPI 加密存本地（%LOCALAPPDATA%\ShoreHue\github_token.dat）。
    /// </summary>
    public static class GitHubMarketService
    {
        // ★ OAuth App 公开客户端（GitHub 设备流本就不需要 client_secret，移除降低泄露面）
        private const string ClientId = "Ov23liuwJVfNqERDFiY4";
        private const string Repo = "timecolors/ShoreHue";

        // ★ 应用访问 GitHub 需走代理（浏览器/插件代理 HttpClient 默认不走）：自动读 git 全局代理配置复用
        private static readonly Lazy<HttpClient> HttpLazy = new(CreateClient);
        private static HttpClient Http => HttpLazy.Value;
        private static string? _token;
        private static long? _userId;
        private static string? _activeProxy;

        private static string LogFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShoreHue", "github_login.log");

        /// <summary>追加一行登录调试日志。</summary>
        internal static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.AppendAllText(LogFile, DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine);
            }
            catch { }
        }

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler();
            try
            {
                string? proxy = GetGitProxy();
                if (!string.IsNullOrEmpty(proxy) && Uri.TryCreate(proxy, UriKind.Absolute, out var pu))
                {
                    handler.Proxy = new WebProxy(pu);
                    handler.UseProxy = true;
                    _activeProxy = proxy;
                }
            }
            catch { }
            Log("HttpClient 初始化，代理=" + (_activeProxy ?? "（无，直连/系统代理）"));
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>读取 git 全局 http 代理（如 http://127.0.0.1:4088）。</summary>
        private static string? GetGitProxy()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", "config --global http.proxy")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return null;
                if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return null; }
                string? url = p.StandardOutput.ReadToEnd()?.Trim();
                return string.IsNullOrEmpty(url) ? null : url;
            }
            catch { return null; }
        }

        public static bool IsLoggedIn => !string.IsNullOrEmpty(_token);
        public static string? CurrentUser { get; private set; }
        /// <summary>当前登录用户的 GitHub 数字 ID（不可变、不可伪造，删除/身份校验用）。</summary>
        public static long? CurrentUserId { get; private set; }

        private static string TokenFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShoreHue", "github_token.dat");

        /// <summary>启动时尝试从本地加载已登录 token。</summary>
        public static async Task TryLoadTokenAsync()
        {
            try
            {
                if (!File.Exists(TokenFile)) return;
                byte[] enc = File.ReadAllBytes(TokenFile);
                string token = Encoding.UTF8.GetString(ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser));
                if (string.IsNullOrEmpty(token)) return;
                string? user = await GetUserAsync(token);
                if (user != null) { _token = token; CurrentUser = user; CurrentUserId = _userId; }
            }
            catch { }
        }

        /// <summary>开始设备流：返回 (验证码, 授权网址, device_code)。</summary>
        public static async Task<(string userCode, string verificationUri, string deviceCode)> StartDeviceFlowAsync()
        {
            Log("开始设备流：POST https://github.com/login/device/code");
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                // ★ 最小权限：public_repo 即可读写公开市场仓库；repo 会授权全部公开+私有仓库（泄露影响面大）
                ["scope"] = "public_repo"
            });
            var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code");
            req.Content = form;
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage resp;
            try
            {
                resp = await Http.SendAsync(req);
            }
            catch (Exception ex)
            {
                Log("设备流请求异常: " + ex.GetType().Name + " " + ex.Message + (ex.InnerException != null ? " / inner: " + ex.InnerException.Message : ""));
                throw;
            }
            using (resp)
            {
                string json = await resp.Content.ReadAsStringAsync();
                // ★ 安全：不把响应正文写日志（含 device_code/user_code，且 device_code 可用于轮询换 token）
                Log("设备流响应 " + (int)resp.StatusCode + "（正文脱敏）");
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"GitHub 设备流返回 {(int)resp.StatusCode} {resp.ReasonPhrase}");
                using var doc = ParseJsonOrForm(json);
                var root = doc.RootElement;
                return (
                    root.GetProperty("user_code").GetString() ?? "",
                    root.GetProperty("verification_uri").GetString() ?? "https://github.com/login/device",
                    root.GetProperty("device_code").GetString() ?? "");
            }
        }

        /// <summary>GitHub 设备流接口可能返回 JSON 或 form 编码，统一解析为 JSON。</summary>
        private static JsonDocument ParseJsonOrForm(string text)
        {
            if (text.TrimStart().StartsWith("{")) return JsonDocument.Parse(text);
            var dict = new Dictionary<string, string>();
            foreach (var pair in text.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                dict[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
            string json = JsonSerializer.Serialize(dict);
            return JsonDocument.Parse(json);
        }

        /// <summary>轮询换取 token（按 GitHub 建议间隔）。true = 成功；false = 仍在等待授权。</summary>
        public static async Task<bool> PollForTokenAsync(string deviceCode, int intervalSec)
        {
            await Task.Delay(Math.Max(2, intervalSec) * 1000);
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_code"] = deviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            });
            var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token");
            req.Content = form;
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage resp;
            try
            {
                resp = await Http.SendAsync(req);
            }
            catch (Exception ex)
            {
                Log("轮询请求异常: " + ex.GetType().Name + " " + ex.Message + (ex.InnerException != null ? " / inner: " + ex.InnerException.Message : ""));
                throw;
            }
            using (resp)
            {
                string json = await resp.Content.ReadAsStringAsync();
                // ★ 安全：轮询响应含 access_token，绝不写日志（只记录状态码）
                Log("轮询响应 " + (int)resp.StatusCode + "（正文脱敏）");
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"GitHub 授权轮询返回 {(int)resp.StatusCode} {resp.ReasonPhrase}");
                using var doc = ParseJsonOrForm(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("access_token", out var t))
                {
                    _token = t.GetString();
                    Log("获取到 token，查询用户信息…");
                    CurrentUser = await GetUserAsync(_token);
                    CurrentUserId = _userId;
                    Log("登录用户: " + (CurrentUser ?? "（未知）") + " (id=" + (CurrentUserId?.ToString() ?? "?") + ")");
                    SaveToken();
                    return true;
                }
                // ★ 遵守 GitHub 轮询节流：slow_down 时下次等待 +5 秒（防限流 403）
                if (root.TryGetProperty("error", out var err) && err.GetString() == "slow_down")
                {
                    await Task.Delay(5000);
                }
                return false;   // authorization_pending / slow_down 等
            }
        }

        public static void Logout()
        {
            _token = null; CurrentUser = null; CurrentUserId = null; _userId = null;
            try { if (File.Exists(TokenFile)) File.Delete(TokenFile); } catch { }
        }

        private static void SaveToken()
        {
            try
            {
                if (string.IsNullOrEmpty(_token)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(TokenFile)!);
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(_token), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(TokenFile, enc);
            }
            catch { }
        }

        private static async Task<string?> GetUserAsync(string token)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.UserAgent.ParseAdd("ShoreHue");
                using (var resp = await Http.SendAsync(req))
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    Log("用户信息响应 " + (int)resp.StatusCode + "（正文脱敏）");
                    if (!resp.IsSuccessStatusCode) return null;
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    // ★ 保存 GitHub 数字 ID（不可变身份，删除/发布校验用）
                    if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                    {
                        _userId = idEl.GetInt64();
                    }
                    return root.TryGetProperty("login", out var l) ? l.GetString() : null;
                }
            }
            catch (Exception ex)
            {
                Log("用户信息请求异常: " + ex.GetType().Name + " " + ex.Message + (ex.InnerException != null ? " / inner: " + ex.InnerException.Message : ""));
                return null;
            }
        }

        private static string Truncate(string s, int max)
        {
            return string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");
        }

        /// <summary>删除市场包：删 main.cs/manifest.json + 从 index.json 移除。返回 null=成功，否则错误信息。</summary>
        public static async Task<string?> DeletePackageAsync(string id)
        {
            if (string.IsNullOrEmpty(_token)) return "未登录 GitHub";
            // ★ 安全：包 id 仅允许 英文/数字/下划线/连字符（防路径穿越，与 WidgetPluginStore.IsValidId 一致）
            if (string.IsNullOrEmpty(id) || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{2,64}$"))
                return "非法包 id";
            try
            {
                // ★ 按 manifest 的 files 清单删除全部文件（兼容多形态包）
                var toDelete = new System.Collections.Generic.List<string> { "manifest.json", "main.cs" };
                var mfInfo = await GetContentAsync($"market/packages/{id}/manifest.json");
                if (mfInfo != null)
                {
                    try
                    {
                        var doc = System.Text.Json.JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(mfInfo.Value.Content)));
                        if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var f in files.EnumerateArray()) { var s = f.GetString(); if (!string.IsNullOrWhiteSpace(s)) toDelete.Add(s); }
                    }
                    catch { }
                }
                foreach (var file in toDelete.Distinct())
                {
                    var info = await GetContentAsync($"market/packages/{id}/{file}");
                    if (info != null) await DeleteFileAsync($"market/packages/{id}/{file}", info.Value.Sha);
                }
                var idx = await GetContentAsync("market/index.json");
                if (idx != null)
                {
                    string content = Encoding.UTF8.GetString(Convert.FromBase64String(idx.Value.Content));
                    string updated = RemovePackageFromIndex(content, id);
                    await PutFileAsync("market/index.json", updated, idx.Value.Sha, $"删除市场包 {id}");
                }
                return null;
            }
            catch (Exception ex) { return "删除失败：" + ex.Message; }
        }

        private static string RemovePackageFromIndex(string indexJson, string id)
        {
            using var doc = JsonDocument.Parse(indexJson);
            var root = doc.RootElement;
            var packages = new List<object?>();
            if (root.TryGetProperty("packages", out var pkgs) && pkgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pkgs.EnumerateArray())
                {
                    string? pid = p.TryGetProperty("id", out var idv) ? idv.GetString() : null;
                    if (pid != id)
                    {
                        var dict = new Dictionary<string, object?>();
                        foreach (var prop in p.EnumerateObject())
                        {
                            object? val = null;
                            switch (prop.Value.ValueKind)
                            {
                                case JsonValueKind.String: val = prop.Value.GetString(); break;
                                case JsonValueKind.Number: val = prop.Value.GetDouble(); break;
                                case JsonValueKind.True: val = true; break;
                                case JsonValueKind.False: val = false; break;
                                case JsonValueKind.Array: val = JsonSerializer.Deserialize<object[]>(prop.Value.GetRawText()); break;
                            }
                            dict[prop.Name] = val;
                        }
                        packages.Add(dict);
                    }
                }
            }
            var outRoot = new Dictionary<string, object?>
            {
                ["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd"),
                ["marketBase"] = "https://cdn.jsdelivr.net/gh/timecolors/ShoreHue@master/market",
                ["packages"] = packages
            };
            return JsonSerializer.Serialize(outRoot, new JsonSerializerOptions { WriteIndented = true });
        }

        private static async Task<(string Content, string Sha)?> GetContentAsync(string path)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/contents/{Uri.EscapeDataString(path)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            req.Headers.UserAgent.ParseAdd("ShoreHue");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using (var resp = await Http.SendAsync(req))
            {
                if (!resp.IsSuccessStatusCode) return null;
                string json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                string content = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                string sha = doc.RootElement.TryGetProperty("sha", out var s) ? s.GetString() ?? "" : "";
                return (content, sha);
            }
        }

        private static async Task DeleteFileAsync(string path, string sha)
        {
            var req = new HttpRequestMessage(HttpMethod.Delete, $"https://api.github.com/repos/{Repo}/contents/{Uri.EscapeDataString(path)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            req.Headers.UserAgent.ParseAdd("ShoreHue");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["message"] = $"删除市场包文件 {path}",
                ["sha"] = sha
            }), Encoding.UTF8, "application/json");
            using (var resp = await Http.SendAsync(req))
            {
                resp.EnsureSuccessStatusCode();
            }
        }

        private static async Task PutFileAsync(string path, string content, string sha, string message)
        {
            var req = new HttpRequestMessage(HttpMethod.Put, $"https://api.github.com/repos/{Repo}/contents/{Uri.EscapeDataString(path)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            req.Headers.UserAgent.ParseAdd("ShoreHue");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["message"] = message,
                ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
                ["sha"] = sha
            }), Encoding.UTF8, "application/json");
            using (var resp = await Http.SendAsync(req))
            {
                resp.EnsureSuccessStatusCode();
            }
        }

        // ==================== 放流：发布包到市场 ====================

        /// <summary>包内一个文件（文件名 + 内容），用于多形态（纯代码 / XAML / 整套预设 / 配置）。</summary>
        public sealed record PackageFile(string Name, string Content);

        /// <summary>发布一个包到市场（写 manifest.json + files 清单 + 更新 index.json）。返回 null=成功，否则错误信息。</summary>
        public static async Task<string?> PublishPackageAsync(
            string id, string name, string kind, string category, string version,
            string description, string baseType, string parentKey, string sourceKey,
            List<string> permissions, string source,
            System.Collections.Generic.List<PackageFile>? extraFiles = null)
        {
            if (string.IsNullOrEmpty(_token)) return "未登录 GitHub";
            // ★ 安全：包 id 仅允许 英文/数字/下划线/连字符（防路径穿越）
            if (string.IsNullOrEmpty(id) || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{2,64}$"))
                return "非法包 id（仅英文/数字/下划线/连字符）";
            if (string.IsNullOrEmpty(source)) return "源码为空";
            try
            {
                // 1) manifest.json（含权限检测）
                var allFiles = new System.Collections.Generic.List<PackageFile>
                {
                    new PackageFile("main.cs", source)
                };
                if (extraFiles != null) foreach (var f in extraFiles)
                {
                    if (!string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Content)
                        && !allFiles.Exists(x => string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase)))
                        allFiles.Add(f);
                }
                var manifest = new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["kind"] = kind,
                    ["category"] = category,
                    ["version"] = string.IsNullOrEmpty(version) ? "1.0.0" : version,
                    ["author"] = CurrentUser ?? "",
                    // ★ 发布者 GitHub 数字 ID（不可变身份；删除时用 id 校验，防 author 字段伪造）
                    ["publisherId"] = CurrentUserId ?? 0,
                    ["description"] = description ?? "",
                    ["baseType"] = baseType,
                    ["parentKey"] = parentKey,
                    ["sourceKey"] = sourceKey,
                    ["permissions"] = permissions ?? new List<string>(),
                    // ★ 包协议版本：客户端按此拒绝高于自身 SupportedApiVersion 的新包（防格式演进破坏旧客户端）
                    ["apiVersion"] = 1,
                    // ★ 文件清单：下载端按此逐个拉取；形态 = 是否含 .xaml
                    ["files"] = allFiles.Select(f => f.Name).ToList()
                };
                string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

                // 2) 已存在则取 sha（覆盖更新），否则创建
                string dir = $"market/packages/{id}";
                string mfSha = "";
                var mf = await GetContentAsync(dir + "/manifest.json");
                if (mf != null) mfSha = mf.Value.Sha;
                await PutFileAsync(dir + "/manifest.json", manifestJson, mfSha, $"发布市场包 {id}（manifest）");

                foreach (var f in allFiles)
                {
                    string fileSha = "";
                    var existing = await GetContentAsync(dir + "/" + f.Name);
                    if (existing != null) fileSha = existing.Value.Sha;
                    await PutFileAsync(dir + "/" + f.Name, f.Content, fileSha, $"发布市场包 {id}（{f.Name}）");
                }

                // 3) 更新 index.json（读 → 加/改条目 → 写）
                var idx = await GetContentAsync("market/index.json");
                string indexJson = "{}";
                string idxSha = "";
                if (idx != null)
                {
                    indexJson = Encoding.UTF8.GetString(Convert.FromBase64String(idx.Value.Content));
                    idxSha = idx.Value.Sha;
                }
                string updated = UpsertPackageInIndex(indexJson, id, name, kind, category, version,
                    CurrentUser ?? "", description ?? "", baseType, parentKey, sourceKey, permissions);
                await PutFileAsync("market/index.json", updated, idxSha, $"发布市场包 {id}（索引）");
                return null;
            }
            catch (Exception ex) { return "放流失败：" + ex.Message; }
        }

        /// <summary>在 index.json 中新增或覆盖一个包条目（保留其他字段完整，用 JsonNode 深拷贝）。</summary>
        private static string UpsertPackageInIndex(string indexJson, string id, string name, string kind, string category,
            string version, string author, string description, string baseType, string parentKey, string sourceKey,
            List<string> permissions)
        {
            using var doc = JsonDocument.Parse(indexJson);
            var root = doc.RootElement;
            var packages = new List<object?>();
            bool replaced = false;
            if (root.TryGetProperty("packages", out var pkgs) && pkgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pkgs.EnumerateArray())
                {
                    string? pid = p.TryGetProperty("id", out var idv) ? idv.GetString() : null;
                    if (pid == id)
                    {
                        var rebuilt = BuildPackageEntry(id, name, kind, category, version, author, description, baseType, parentKey, sourceKey, permissions);
                        // ★ 官方验证状态与协议版本随包保留：重新发布/更新不能抹掉 official / apiVersion
                        if (p.TryGetProperty("official", out var off) && off.ValueKind == JsonValueKind.True)
                            rebuilt["official"] = true;
                        if (p.TryGetProperty("apiVersion", out var av) && av.ValueKind == JsonValueKind.Number)
                            rebuilt["apiVersion"] = av.GetInt32();
                        packages.Add(rebuilt);
                        replaced = true;
                    }
                    else packages.Add(ParseEntry(p));
                }
            }
            if (!replaced)
                packages.Add(BuildPackageEntry(id, name, kind, category, version, author, description, baseType, parentKey, sourceKey, permissions));

            var outRoot = new Dictionary<string, object?>
            {
                ["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd"),
                ["marketBase"] = "https://cdn.jsdelivr.net/gh/timecolors/ShoreHue@master/market",
                ["packages"] = packages
            };
            return JsonSerializer.Serialize(outRoot, new JsonSerializerOptions { WriteIndented = true });
        }

        private static Dictionary<string, object?> BuildPackageEntry(string id, string name, string kind, string category,
            string version, string author, string description, string baseType, string parentKey, string sourceKey,
            List<string> permissions)
        {
            return new Dictionary<string, object?>
            {
                ["id"] = id, ["name"] = name, ["kind"] = kind, ["category"] = category,
                ["version"] = version, ["author"] = author, ["description"] = description,
                ["baseType"] = baseType, ["parentKey"] = parentKey, ["sourceKey"] = sourceKey,
                ["permissions"] = permissions ?? new List<string>(),
                ["apiVersion"] = 1,
                ["publisherId"] = GitHubMarketService.CurrentUserId ?? 0
            };
        }

        /// <summary>完整保留包条目的所有字段（含未知新字段）。</summary>
        private static Dictionary<string, object?> ParseEntry(JsonElement p)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var prop in p.EnumerateObject())
            {
                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.String: dict[prop.Name] = prop.Value.GetString(); break;
                    case JsonValueKind.Number: dict[prop.Name] = prop.Value.GetRawText(); break;
                    case JsonValueKind.True: dict[prop.Name] = true; break;
                    case JsonValueKind.False: dict[prop.Name] = false; break;
                    case JsonValueKind.Array:
                        var arr = new List<object?>();
                        foreach (var e in prop.Value.EnumerateArray())
                        {
                            if (e.ValueKind == JsonValueKind.String) arr.Add(e.GetString());
                            else arr.Add(e.GetRawText());
                        }
                        dict[prop.Name] = arr; break;
                    case JsonValueKind.Object: dict[prop.Name] = prop.Value.GetRawText(); break;
                }
            }
            return dict;
        }

        // ==================== 个人设置云同步 ====================

        /// <summary>云配置路径：configs/&lt;user&gt;.json（GitHub 仓库内，个人私有配置区）。</summary>
        public static string CloudConfigPath(string user) => $"configs/{SanitizeUser(user)}.json";

        /// <summary>用户名 → 安全文件名（只留 字母/数字/下划线/连字符，防路径注入）。</summary>
        private static string SanitizeUser(string user)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in (user ?? ""))
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            }
            return sb.Length == 0 ? "unknown" : sb.ToString();
        }

        /// <summary>上传个人设置到云端。返回 null=成功，否则错误信息。</summary>
        public static async Task<string?> UploadConfigAsync(string user, string json)
        {
            if (string.IsNullOrEmpty(_token)) return "未登录 GitHub";
            if (string.IsNullOrEmpty(user)) return "缺少用户名";
            try
            {
                string path = CloudConfigPath(user);
                string sha = "";
                var existing = await GetContentAsync(path);
                if (existing != null) sha = existing.Value.Sha;
                await PutFileAsync(path, json, sha, "ShoreHue 设置云同步（上传）");
                return null;
            }
            catch (Exception ex) { return "上传失败：" + ex.Message; }
        }

        /// <summary>从云端下载个人设置。返回 (json, null)=成功；(null, 错误)=失败或云端无备份。</summary>
        public static async Task<(string? Json, string? Error)> DownloadConfigAsync(string user)
        {
            if (string.IsNullOrEmpty(_token)) return (null, "未登录 GitHub");
            if (string.IsNullOrEmpty(user)) return (null, "缺少用户名");
            try
            {
                var info = await GetContentAsync(CloudConfigPath(user));
                if (info == null) return (null, "云端没有你的设置备份（请先上传）");
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(info.Value.Content));
                return (json, null);
            }
            catch (Exception ex) { return (null, "下载失败：" + ex.Message); }
        }

        /// <summary>云端是否有该用户的设置备份。null=无/失败。</summary>
        public static async Task<bool?> HasConfigAsync(string user)
        {
            if (string.IsNullOrEmpty(user)) return null;
            try { return (await GetContentAsync(CloudConfigPath(user))) != null; }
            catch { return null; }
        }
    }
}
