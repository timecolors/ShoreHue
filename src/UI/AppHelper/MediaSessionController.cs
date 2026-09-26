using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace ShoreHue.UI.AppHelper
{
    /// <summary>
    /// 单个正在播放的媒体会话（来自 QQ 音乐、浏览器等注册了系统媒体会话的应用）。
    /// </summary>
    public class MediaSessionInfo
    {
        public GlobalSystemMediaTransportControlsSession Session { get; }
        public string AppName { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public bool IsPlaying { get; set; }
        public ImageSource? Thumbnail { get; set; }

        public MediaSessionInfo(GlobalSystemMediaTransportControlsSession session)
        {
            Session = session;
        }

        public string DisplayText =>
            string.IsNullOrEmpty(Title)
                ? AppName
                : (string.IsNullOrEmpty(Artist) ? Title : $"{Title} · {Artist}");
    }

    /// <summary>
    /// 系统媒体会话控制：枚举并控制其他应用（如 QQ 音乐）的播放。
    /// 基于 GlobalSystemMediaTransportControlsSession（Windows 10 1809+）。
    /// </summary>
    public static class MediaSessionController
    {
        private static GlobalSystemMediaTransportControlsSessionManager? _manager;

        public static async Task<List<MediaSessionInfo>> GetSessionsAsync()
        {
            var result = new List<MediaSessionInfo>();
            try
            {
                if (_manager == null)
                {
                    _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
                }
                if (_manager == null) return result;

                foreach (var session in _manager.GetSessions())
                {
                    var info = new MediaSessionInfo(session)
                    {
                        AppName = FriendlyAppName(session.SourceAppUserModelId)
                    };

                    try
                    {
                        var props = await session.TryGetMediaPropertiesAsync().AsTask();
                        info.Title = props.Title ?? "";
                        info.Artist = props.Artist ?? "";

                        // ★ 专辑封面：从媒体属性读取缩略图流
                        if (props.Thumbnail != null)
                        {
                            try
                            {
                                using var thumbStream = await props.Thumbnail.OpenReadAsync().AsTask();
                                // ★ WinRT 流可能不支持随机访问，先复制到 MemoryStream 再解码
                                using var ms = new MemoryStream();
                                await thumbStream.AsStreamForRead().CopyToAsync(ms);
                                if (ms.Length > 0)
                                {
                                    ms.Position = 0;
                                    var bitmap = new BitmapImage();
                                    bitmap.BeginInit();
                                    bitmap.StreamSource = ms;
                                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                    bitmap.DecodePixelWidth = 96;
                                    bitmap.EndInit();
                                    bitmap.Freeze();
                                    info.Thumbnail = bitmap;
                                }
                            }
                            catch (Exception ex)
                            {
                                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug(
                                    $"[MediaCover] 读取封面失败: {ex.Message}");
                            }
                        }
                        else
                        {
                            ShoreHue.Core.Infrastructure.Logging.LogManager.Debug(
                                $"[MediaCover] {info.AppName} 媒体属性无缩略图");
                        }
                    }
                    catch { /* 尽力而为：这个会话读不到属性就跳过它，其余会话照常列出 */ }

                    try
                    {
                        var playback = session.GetPlaybackInfo();
                        info.IsPlaying =
                            playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    }
                    catch { /* 读不到播放状态就当"未在播放"（界面最多少亮一个标记） */ }

                    result.Add(info);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取媒体会话失败: {ex.Message}");
            }
            return result;
        }

        public static async Task<bool> TogglePlayPauseAsync(MediaSessionInfo info)
        {
            try { return await info.Session.TryTogglePlayPauseAsync().AsTask(); }
            catch { return false; }
        }

        public static async Task<bool> NextAsync(MediaSessionInfo info)
        {
            try { return await info.Session.TrySkipNextAsync().AsTask(); }
            catch { return false; }
        }

        public static async Task<bool> PrevAsync(MediaSessionInfo info)
        {
            try { return await info.Session.TrySkipPreviousAsync().AsTask(); }
            catch { return false; }
        }

        public static async Task<bool> PlayAsync(MediaSessionInfo info)
        {
            try { return await info.Session.TryPlayAsync().AsTask(); }
            catch { return false; }
        }

        public static async Task<bool> PauseAsync(MediaSessionInfo info)
        {
            try { return await info.Session.TryPauseAsync().AsTask(); }
            catch { return false; }
        }

        /// <summary>
        /// 常见应用的 AUMID 前缀 → 显示名映射。
        /// AUMID 形如 "Tencent.QQMusic.xxx"，前缀匹配映射名，避免显示成 "Tencent"。
        /// 匹配规则：AUMID 按 "AppId!" 分割取应用段，再按 "." 前缀匹配映射表；
        /// 未命中时回退取第一段（原行为）。
        /// </summary>
        private static readonly (string Prefix, string Name)[] AppNameMap =
        {
            ("Tencent.QQMusic", "QQ 音乐"),
            ("Tencent.QQ", "QQ"),
            ("Tencent.QiDian", "起点读书"),
            ("SpotifyAB.SpotifyMusic", "Spotify"),
            ("Spotify", "Spotify"),
            ("NetEase", "网易云音乐"),
            ("CloudMusic", "网易云音乐"),
            ("Kugou", "酷狗音乐"),
            ("KuWo", "酷我音乐"),
            ("1Password", "1Password"),
            ("Google.Chrome", "Chrome"),
            ("Chrome", "Chrome"),
            ("MSEdge", "Edge"),
            ("Microsoft.Edge", "Edge"),
            ("Mozilla.Firefox", "Firefox"),
            ("Firefox", "Firefox"),
            ("BraveSoftware.BraveBrowser", "Brave"),
            ("VLC", "VLC"),
            ("VideoLAN", "VLC 播放器"),
            ("PotPlayer", "PotPlayer"),
            ("DAUM.PotPlayer", "PotPlayer"),
            ("PotPlayerMini", "PotPlayer"),
            ("Foobar2000", "foobar2000"),
            ("foobar2000", "foobar2000"),
            ("AIMP", "AIMP"),
            ("MusicBee", "MusicBee"),
            ("SPlayer", "迅雷看看"),
            ("Xunlei", "迅雷"),
            ("bilibili", "哔哩哔哩"),
            ("BiliBili", "哔哩哔哩"),
            ("Douyin", "抖音"),
            ("Youtube", "YouTube"),
            ("YouTube", "YouTube"),
            ("Twitch", "Twitch"),
            ("Plex", "Plex"),
            ("Emby", "Emby"),
            ("Jellyfin", "Jellyfin"),
            ("Snap", "Snap 应用"),
            ("WeGame", "WeGame"),
            ("Steam", "Steam"),
            ("EAC", "EasyAntiCheat"),
            ("Zoom", "Zoom"),
            ("Teams", "Teams"),
            ("Microsoft.Teams", "Teams"),
            ("DingTalk", "钉钉"),
            ("Feishu", "飞书"),
            ("Lark", "飞书"),
            ("WeCom", "企业微信"),
            ("企业微信", "企业微信"),
            ("Weixin", "微信"),
            ("WeChat", "微信"),
            ("dingtalk", "钉钉"),
        };

        private static string FriendlyAppName(string sourceAppUserModelId)
        {
            // ★ 复用已有的本地化键（Toast_UnknownApp 就是"未知应用"/"Unknown app"）：
            //   硬编码中文会让英文界面里出现中文，而同义的键本来就已经存在。
            if (string.IsNullOrEmpty(sourceAppUserModelId))
                return ShoreHue.UI.Localization.LocalizationManager.Instance["Toast_UnknownApp"];

            // 形如 "Tencent.QQMusic.xxx" 或 "SpotifyAB.SpotifyMusic_zhtwkyt98bp6g!App"
            string app = sourceAppUserModelId;
            int bang = app.IndexOf('!');
            if (bang > 0) app = app[..bang];

            foreach (var (prefix, name) in AppNameMap)
            {
                if (app.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return name;
            }

            // 未命中：取第一段（原行为）
            int dot = app.IndexOf('.');
            if (dot > 0 && dot < app.Length - 1)
            {
                string head = app[..dot];
                if (!head.Contains('!') && !head.Contains('{'))
                    return head;
            }
            return app;
        }
    }
}
