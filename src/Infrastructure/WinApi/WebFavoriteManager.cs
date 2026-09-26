using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Infrastructure.Utils;

namespace ShoreHue.Infrastructure.WinApi
{
    /// <summary>
    /// 常用网页与最近打开的网页：
    ///  - 常用网页：用户手动保存（可增删）；
    ///  - 最近打开：面板内打开记录 + Edge/Chrome 历史 + IE TypedURLs 兜底。
    /// </summary>
    public static class WebFavoriteManager
    {
        public sealed class WebEntry
        {
            public string Url { get; set; } = "";
            public string Title { get; set; } = "";
            public DateTime LastVisit { get; set; } = DateTime.Now;
            public bool IsFavorite { get; set; }
        }

        private static readonly string FavoritesPath = AppPaths.FavoritesPath;
        private static readonly string RecentPath = AppPaths.RecentWebsPath;

        private static readonly object _lock = new();

        private static List<WebEntry> _favorites = new();
        private static List<WebEntry> _recentOpens = new();
        private static bool _loaded;

        public static IReadOnlyList<WebEntry> Favorites => _favorites;

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                LoadFavorites();
                LoadRecentOpens();
                _loaded = true;
            }
        }

        // ================= 常用网页 =================

        public static bool AddFavorite(string url)
        {
            EnsureLoaded();
            url = NormalizeUrl(url);
            if (string.IsNullOrEmpty(url)) return false;

            lock (_lock)
            {
                if (_favorites.Any(f => f.Url.Equals(url, StringComparison.OrdinalIgnoreCase))) return false;
                _favorites.Insert(0, new WebEntry
                {
                    Url = url,
                    Title = GetDomain(url),
                    LastVisit = DateTime.Now,
                    IsFavorite = true
                });
                SaveFavorites();
                return true;
            }
        }

        public static void RemoveFavorite(string url)
        {
            // ★ 必须先 EnsureLoaded：`_favorites` 在首次触碰本类时是**空列表**，
            //   此处直接 SaveFavorites() 会把用户的收藏文件覆盖成 `[]`（真实数据丢失）。
            //   本方法可以从插件能力 HostCapabilities.RemoveWebFavorite 走到，
            //   也就是"外来代码首次触碰本类"是可达路径。
            //   同文件里 AddFavorite / IsFavorite / RecordOpen 都调了 EnsureLoaded，只有这里漏了。
            EnsureLoaded();
            lock (_lock)
            {
                _favorites.RemoveAll(f => f.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
                SaveFavorites();
            }
        }

        public static bool IsFavorite(string url)
        {
            EnsureLoaded();
            lock (_lock)
            {
                return _favorites.Any(f => f.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            }
        }

        // ================= 最近打开 =================

        public static void RecordOpen(string url, string? title = null)
        {
            EnsureLoaded();
            url = NormalizeUrl(url);
            if (string.IsNullOrEmpty(url)) return;

            lock (_lock)
            {
                _recentOpens.RemoveAll(r => r.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
                _recentOpens.Insert(0, new WebEntry
                {
                    Url = url,
                    Title = string.IsNullOrWhiteSpace(title) ? GetDomain(url) : title,
                    LastVisit = DateTime.Now
                });
                while (_recentOpens.Count > 40) _recentOpens.RemoveAt(_recentOpens.Count - 1);
                SaveRecentOpens();
            }
        }

        /// <summary>
        /// 合并常用 + 最近打开 + 浏览器历史，按常用优先、最近时间排序去重。
        /// </summary>
        public static List<WebEntry> GetCombined(int max = 40)
        {
            EnsureLoaded();

            var all = new Dictionary<string, WebEntry>(StringComparer.OrdinalIgnoreCase);
            // ★ 与 AddFavorite/RecordOpen 共用同一把锁：本方法会被 RecentItemsView 用 Task.Run 调到后台线程，
            //   而 RecordOpen 可能在 UI 线程同时改这两个表 —— 以前这里裸遍历 + 改字段，
            //   既可能读到半更新的集合，也会和写侧互相踩。
            //   锁内只做内存合并；浏览器历史/输入记录那些 I/O 放到锁外。
            lock (_lock)
            {
                foreach (var f in _favorites)
                {
                    f.IsFavorite = true;
                    all[f.Url] = f;
                }
                foreach (var r in _recentOpens)
                {
                    if (all.TryGetValue(r.Url, out var existing))
                    {
                        if (r.LastVisit > existing.LastVisit) existing.LastVisit = r.LastVisit;
                    }
                    else
                    {
                        all[r.Url] = r;
                    }
                }
            }

            foreach (var b in GetRecentFromBrowsers(25))
            {
                if (all.TryGetValue(b.Url, out var existing))
                {
                    if (b.LastVisit > existing.LastVisit) existing.LastVisit = b.LastVisit;
                    if (string.IsNullOrEmpty(existing.Title)) existing.Title = b.Title;
                }
                else
                {
                    all[b.Url] = b;
                }
            }
            foreach (var t in GetTypedUrls(10))
            {
                if (!all.ContainsKey(t.Url))
                {
                    all[t.Url] = t;
                }
            }

            return all.Values
                .OrderByDescending(e => e.IsFavorite)
                .ThenByDescending(e => e.LastVisit)
                .Take(max)
                .ToList();
        }

        // ================= 浏览器历史 =================

        private static List<WebEntry> GetRecentFromBrowsers(int max)
        {
            var result = new List<WebEntry>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates =
            {
                Path.Combine(local, @"Microsoft\Edge\User Data\Default\History"),
                Path.Combine(local, @"Google\Chrome\User Data\Default\History"),
                Path.Combine(local, @"Microsoft\Edge\User Data\Profile 1\History"),
                Path.Combine(local, @"Google\Chrome\User Data\Profile 1\History")
            };

            foreach (string db in candidates)
            {
                if (!File.Exists(db)) continue;
                string? tempCopy = null;
                try
                {
                    // Edge/Chrome 运行时会锁定 History，直接打开会报 database is locked；
                    // 复制到临时目录再以只读方式读取（浏览器历史的标准做法）。
                    tempCopy = Path.Combine(Path.GetTempPath(),
                        "db_history_" + Guid.NewGuid().ToString("N") + ".sqlite");
                    File.Copy(db, tempCopy, true);

                    using var conn = new SqliteConnection(
                        $"Data Source={tempCopy};Mode=ReadOnly;Pooling=False;Default Timeout=2");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "SELECT url, title, last_visit_time FROM urls " +
                        "WHERE url LIKE 'http%' AND url NOT LIKE '%google.com/search%' " +
                        "ORDER BY last_visit_time DESC LIMIT @max";
                    cmd.Parameters.AddWithValue("@max", max);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        string url = reader.GetString(0);
                        string title = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        long chromeTime = reader.GetInt64(2);
                        DateTime time = chromeTime > 0
                            ? DateTime.FromFileTimeUtc(chromeTime * 10).ToLocalTime()
                            : DateTime.MinValue;
                        if (string.IsNullOrWhiteSpace(title)) title = GetDomain(url);
                        result.Add(new WebEntry { Url = url, Title = title, LastVisit = time });
                    }
                }
                catch (Exception ex)
                {
                    // 浏览器历史读不出来 → "最近网页"这一来源为空（用户可见的功能缺失）
                    LogManager.Warning($"[网页收藏] 读取浏览器历史失败（该来源为空）：{ex.Message}");
                }
                finally
                {
                    // 尽力而为：临时副本删不掉会留在 %TEMP%，无害（清理失败不再套一层日志）
                    try { if (tempCopy != null && File.Exists(tempCopy)) File.Delete(tempCopy); }
                    catch { }
                }
            }
            return result;
        }

        private static List<WebEntry> GetTypedUrls(int max)
        {
            var result = new List<WebEntry>();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Internet Explorer\TypedURLs");
                if (key == null) return result;

                var urls = key.GetValueNames()
                    .Where(n => n.StartsWith("url", StringComparison.OrdinalIgnoreCase))
                    .Select(n => key.GetValue(n) as string)
                    .Where(u => !string.IsNullOrEmpty(u) && u.StartsWith("http"))
                    .Take(max);

                foreach (var url in urls)
                {
                    result.Add(new WebEntry { Url = url!, Title = GetDomain(url!), LastVisit = DateTime.MinValue });
                }
            }
            catch (Exception ex)
            {
                // 尽力而为：这只是众多来源之一（IE 输入过的地址），失败时少几条而已
                LogManager.Debug($"[网页收藏] 读取输入过的地址失败（少一个来源）：{ex.Message}");
            }
            return result;
        }

        // ================= 工具 =================

        public static string NormalizeUrl(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            string url = input.Trim();
            if (!url.Contains("://") && url.Contains("."))
            {
                url = "https://" + url;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "";
            }
            return uri.AbsoluteUri;
        }

        public static string GetDomain(string url)
        {
            try
            {
                var uri = new Uri(url);
                return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                    ? uri.Host[4..]
                    : uri.Host;
            }
            catch (Exception ex)
            {
                // 尽力而为：URL 解析不出主机名就直接显示原串
                LogManager.Debug($"[网页收藏] 解析域名失败（显示原始地址）：{ex.Message}");
                return url;
            }
        }

        // ================= 持久化 =================

        private static void LoadFavorites()
        {
            try
            {
                if (!File.Exists(FavoritesPath)) return;
                var list = JsonSerializer.Deserialize<List<WebEntry>>(File.ReadAllText(FavoritesPath));
                _favorites = list ?? new List<WebEntry>();
            }
            catch (Exception ex)
            {
                // 读不出收藏 → 按空处理（界面显示"没有收藏"）；文件仍在，只是本次读失败
                LogManager.Warning($"[网页收藏] 读取收藏失败（按空处理）：{ex.Message}");
                _favorites = new List<WebEntry>();
            }
        }

        private static void LoadRecentOpens()
        {
            try
            {
                if (!File.Exists(RecentPath)) return;
                var list = JsonSerializer.Deserialize<List<WebEntry>>(File.ReadAllText(RecentPath));
                _recentOpens = list ?? new List<WebEntry>();
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[网页收藏] 读取最近打开失败（按空处理）：{ex.Message}");
                _recentOpens = new List<WebEntry>();
            }
        }

        private static void SaveFavorites()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataRoot);
                File.WriteAllText(FavoritesPath, JsonSerializer.Serialize(_favorites));
            }
            catch (Exception ex)
            {
                // 收藏没落盘 = 重启后丢失（数据持久化失败）
                LogManager.Warning($"[网页收藏] 保存收藏失败（重启后会丢失）：{ex.Message}");
            }
        }

        private static void SaveRecentOpens()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataRoot);
                File.WriteAllText(RecentPath, JsonSerializer.Serialize(_recentOpens));
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[网页收藏] 保存最近打开失败（重启后会丢失）：{ex.Message}");
            }
        }
    }
}
