using System;
using ShoreHue.Core.Infrastructure.Logging;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ShoreHue.Infrastructure.WinApi
{
    /// <summary>
    /// 天气服务（Open-Meteo，完全免费、无需 API Key）：
    ///  - 按城市名（geocoding）或 IP 自动定位
    ///  - 当前温度 + 天气码 → 中文描述与图标
    ///  - 15 分钟缓存（按城市区分），失败静默返回 null
    /// 隐私：查询会向 open-meteo.com / ipwho.is 发起请求（仅当用户启用天气时）。
    /// </summary>
    public static class WeatherService
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
        private static (double Lat, double Lon)? _location;
        private static DateTime _locationTime = DateTime.MinValue;
        private static (string City, string Text, string Emoji)? _cached;
        private static DateTime _cacheTime = DateTime.MinValue;
        /// <summary>上面那份缓存是针对哪个**请求键**算出来的（城市名，空串 = IP 自动定位）。</summary>
        private static string? _cacheRequestKey;

        /// <summary>获取天气文本（如 “☀25° 晴”）；失败返回 null。城市留空 = IP 自动定位。</summary>
        public static async Task<(string Text, string Emoji)?> GetWeatherAsync(string city)
        {
            var full = await GetWeatherWithCityAsync(city);
            return full.HasValue ? (full.Value.Text, full.Value.Emoji) : null;
        }

        /// <summary>获取天气（含生效城市名，如 保定 · ☀25° 晴）；失败返回 null。</summary>
        public static async Task<(string City, string Text, string Emoji)?> GetWeatherWithCityAsync(string city)
        {
            string key = city?.Trim() ?? "";
            // ★ 缓存命中判定要用**同一个键**：以前这里比较 `_cached.Value.City == key`，
            //   而 `_cached.Value.City` 是**解析出来的城市名**（IP 定位时 key 是空串、解析结果是"保定"），
            //   两者永远不相等 → 15 分钟缓存形同虚设，每次刷新都发两个 HTTP 请求
            //   （一次定位、一次天气）。改为记录"这次缓存是针对哪个请求键算出来的"。
            if (_cached != null && (DateTime.Now - _cacheTime).TotalMinutes < 15 && _cacheRequestKey == key)
                return _cached;

            try
            {
                var (lat, lon, resolvedCity) = await GetLocationWithCityAsync(key);
                string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat:F4}&longitude={lon:F4}&current=temperature_2m,weather_code&timezone=auto";
                string json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var cur = doc.RootElement.GetProperty("current");
                double temp = cur.GetProperty("temperature_2m").GetDouble();
                int code = cur.GetProperty("weather_code").GetInt32();
                var (emoji, desc) = WeatherCode(code);
                _cached = (resolvedCity, $"{emoji} {temp:F0}° {desc}", emoji);
                _cacheRequestKey = key;
                _cacheTime = DateTime.Now;
                return _cached;
            }
            catch (Exception ex)
            {
                // 返回 null → 状态栏/小组件显示"取不到天气"（用户可见），留痕便于判断是网络还是解析
                LogManager.Warning($"[天气] 获取天气失败（界面将显示取不到）：{ex.Message}");
                return null;
            }
        }

        public sealed class CitySuggestion
        {
            public string Name { get; set; } = "";
            public string Ascii { get; set; } = "";
            public string Label { get; set; } = "";
            public double Latitude { get; set; }
            public double Longitude { get; set; }
            public long Population { get; set; }
        }

        /// <summary>
        /// 城市联想搜索（Open-Meteo geocoding）：
        /// 拉 20 条后在本地按"名称/拼音前缀匹配优先 + 人口降序"排序，
        /// 避免 API 默认排序把同名/音近城市（如 保定 vs 保山）排错。
        /// </summary>
        public static async Task<List<CitySuggestion>> SearchCitiesAsync(string query)
        {
            var empty = new List<CitySuggestion>();
            if (string.IsNullOrWhiteSpace(query)) return empty;
            try
            {
                string q = query.Trim();
                string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(q)}&count=20&language=zh";
                string json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var results)) return empty;

                var list = new List<CitySuggestion>();
                foreach (var r in results.EnumerateArray())
                {
                    string name = r.GetProperty("name").GetString() ?? "";
                    string ascii = r.TryGetProperty("ascii", out var a) ? (a.GetString() ?? "") : "";
                    string? admin = r.TryGetProperty("admin1", out var ad) ? ad.GetString() : null;
                    string? country = r.TryGetProperty("country", out var co) ? co.GetString() : null;
                    list.Add(new CitySuggestion
                    {
                        Name = name,
                        Ascii = ascii,
                        Label = string.Join(" · ", new[] { name, admin, country }.Where(x => !string.IsNullOrWhiteSpace(x))),
                        Latitude = r.GetProperty("latitude").GetDouble(),
                        Longitude = r.GetProperty("longitude").GetDouble(),
                        Population = r.TryGetProperty("population", out var pop) && pop.ValueKind == JsonValueKind.Number
                            ? pop.GetInt64()
                            : 0
                    });
                }

                // 本地排序：名称/拼音前缀匹配优先，其次包含匹配，再按人口降序
                list.Sort((x, y) =>
                {
                    int sx = Score(x, q);
                    int sy = Score(y, q);
                    if (sx != sy) return sx.CompareTo(sy);
                    return y.Population.CompareTo(x.Population);
                });
                return list;
            }
            catch (Exception ex)
            {
                // 城市搜索失败 → 设置页搜不到城市（返回空列表）
                LogManager.Warning($"[天气] 搜索城市失败（列表为空）：{ex.Message}");
                return empty;
            }
        }

        /// <summary>
        /// 匹配得分：精确 / 行政后缀归一化（"保定市" ≡ "保定"）最优先，
        /// 其次前缀、包含；同级按人口降序。
        /// Open-Meteo 中文数据里河北保定市的标准名是"保定市"，而"保定"是云南一个小地名，
        /// 直接查"保定"会把它排在前面——归一化后"保定市[河北]"会优先。
        /// </summary>
        private static int Score(CitySuggestion c, string q)
        {
            string n = c.Name;
            string a = c.Ascii;
            if (string.Equals(n, q, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, q, StringComparison.OrdinalIgnoreCase))
                return 0;

            // 去掉行政后缀后再比（市/县/区/州/盟）
            string n2 = TrimAdminSuffix(n);
            string q2 = TrimAdminSuffix(q);
            if (string.Equals(n2, q2, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (n2.Length > 0 && q2.Length > 0 &&
                (n2.StartsWith(q2, StringComparison.OrdinalIgnoreCase) ||
                 q2.StartsWith(n2, StringComparison.OrdinalIgnoreCase)))
                return 1;

            if (n.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                return 1;
            if (n.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.Contains(q, StringComparison.OrdinalIgnoreCase))
                return 2;
            return 3;
        }

        private static string TrimAdminSuffix(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.TrimEnd('市', '县', '区', '州', '盟', '旗', '镇');
        }

        /// <summary>
        /// 点击状态栏天气 → 用默认浏览器搜索"城市名 + 天气"。
        /// 用搜索引擎比固定网页更通用（任何网络都能打开，且能给出当地预报入口）。
        /// </summary>
        public static async Task OpenForecastPageAsync(string city)
        {
            try
            {
                string query = city?.Trim() ?? "";
                if (string.IsNullOrEmpty(query))
                {
                    // IP 定位：尝试解析出城市名
                    try
                    {
                        var (_, _, resolved) = await GetLocationWithCityAsync("");
                        query = resolved;
                    }
                    catch (Exception ex)
                    {
                        // 尽力而为：取不到定位城市就用兜底搜索词
                        LogManager.Debug($"[天气] 获取定位城市失败（改用默认搜索词）：{ex.Message}");
                    }
                }
                if (string.IsNullOrEmpty(query)) query = "天气预报";
                string url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query + " 天气");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                // 用户点了"查看天气详情"却没打开浏览器
                LogManager.Warning($"[天气] 打开天气页面失败：{ex.Message}");
            }
        }

        public static void ClearCache()
        {
            _cached = null;
            _cacheRequestKey = null;
            _location = null;
        }

        /// <summary>解析坐标与生效城市名：城市参数 → geocoding；空 → IP 定位（ipwho.is）。</summary>
        private static async Task<(double Lat, double Lon, string City)> GetLocationWithCityAsync(string city)
        {
            if (!string.IsNullOrWhiteSpace(city))
            {
                try
                {
                    string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=zh";
                    string json = await _http.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                    {
                        var first = results[0];
                        string resolved = first.GetProperty("name").GetString() ?? city;
                        return (first.GetProperty("latitude").GetDouble(),
                                first.GetProperty("longitude").GetDouble(),
                                resolved);
                    }
                }
                catch (Exception ex)
                {
                    // 解析失败继续往下走 IP 定位兜底（这条路本来就是"先试再退"）
                    LogManager.Debug($"[天气] 解析城市搜索结果失败（改用 IP 定位兜底）：{ex.Message}");
                }
            }

            // IP 定位兜底（缓存坐标 1 小时）
            if (_location != null && (DateTime.Now - _locationTime).TotalHours < 1)
            {
                return (_location.Value.Lat, _location.Value.Lon, "");
            }
            string ipJson = await _http.GetStringAsync("https://ipwho.is/");
            using var ipDoc = JsonDocument.Parse(ipJson);
            if (!ipDoc.RootElement.TryGetProperty("success", out var ok) || ok.GetBoolean() != true ||
                !ipDoc.RootElement.TryGetProperty("latitude", out var latEl) ||
                !ipDoc.RootElement.TryGetProperty("longitude", out var lonEl))
            {
                throw new InvalidOperationException("IP 定位失败");
            }
            var loc = (latEl.GetDouble(), lonEl.GetDouble());
            _location = loc;
            _locationTime = DateTime.Now;
            string cityName = ipDoc.RootElement.TryGetProperty("city", out var cityEl)
                ? (cityEl.GetString() ?? "")
                : "";
            return (loc.Item1, loc.Item2, cityName);
        }

        private static (string Emoji, string Desc) WeatherCode(int code) => code switch
        {
            0 => ("☀", ShoreHue.UI.Localization.LocalizationManager.Instance["Weather_Desc0"]),
            1 => ("☀", ShoreHue.UI.Localization.LocalizationManager.Instance["Weather_Desc1"]),
            2 => ("☁", ShoreHue.UI.Localization.LocalizationManager.Instance["Weather_Desc2"]),
            3 => ("☁", ShoreHue.UI.Localization.LocalizationManager.Instance["Weather_Desc3"]),
            45 or 48 => ("☁", ShoreHue.UI.Localization.LocalizationManager.Instance["Weather_Desc45"]),
            51 or 53 or 55 or 56 or 57 => ("☂", ShoreHue.UI.Localization.LocalizationManager.Instance["Weather_Desc51"]),
            61 or 63 or 65 or 66 or 67 => ("☂", ShoreHue.UI.Localization.LocalizationManager.Instance["Weather_Desc61"]),
            71 or 73 or 75 or 77 => ("❄", ShoreHue.UI.Localization.LocalizationManager.Instance["Weather_Desc71"]),
            80 or 81 or 82 => ("☂", ShoreHue.UI.Localization.LocalizationManager.Instance["Weather_Desc80"]),
            85 or 86 => ("❄", ShoreHue.UI.Localization.LocalizationManager.Instance["Weather_Desc85"]),
            95 => ("⛈", ShoreHue.UI.Localization.LocalizationManager.Instance["Weather_Desc95"]),
            96 or 99 => ("⛈", ShoreHue.UI.Localization.LocalizationManager.Instance["Weather_Desc96"]),
            _ => ("☇", ShoreHue.UI.Localization.LocalizationManager.Instance["Weather_DescUnknown"])
        };
    }
}
