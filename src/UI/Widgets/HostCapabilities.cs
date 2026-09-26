using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Core.Infrastructure.Service;
using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Ai;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.src.core.Services.Clipboard;
using ShoreHue.src.core.Services.Notes;
using ShoreHue.src.core.Services.Shortcuts;

namespace ShoreHue.UI.Widgets
{
    /// <summary>宿主提供的「最近使用 / 网页收藏」条目（脱敏 DTO：只给展示与打开所需字段）。</summary>
    public sealed class HostRecentItem
    {
        /// <summary>App / File / Web。</summary>
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>可交给 <see cref="HostCapabilities.OpenExternally"/> 打开的目标（路径或网址）。</summary>
        public string Target { get; set; } = "";
    }

    /// <summary>
    /// ★ 沙箱内的宿主能力窄接口（市场/外来代码唯一的特权通道）。
    ///
    /// 背景：沙箱拦的是「语言能力」（Process/反射/文件写…），但宿主自己的 public API
    /// （去卸载、读明文 API Key、读浏览器历史、模拟按键…）同样危险，而且更容易被调用。
    /// v2 起这些宿主特权类型全部进沙箱黑名单，市场代码只能通过本类获取**经过校验的窄能力**：
    ///   · 打开目标 → 白名单协议（http/https/ms-*）或宿主「最近使用」清单里出现过的真实路径；
    ///   · 通知 → 有频率上限；
    ///   · 划词 / AI → 由宿主自己读取与请求，密钥不出宿主；
    ///   · 服务 → 只暴露接口（不含 TrayIcon/ServiceManager 这类能力入口）。
    /// </summary>
    public static class HostCapabilities
    {
        // ==================== 打开外部目标 ====================

        private static readonly object _gate = new();
        private static readonly Queue<DateTime> _toastTimes = new();
        private static List<string> _knownTargets = new();
        private static DateTime _knownTargetsAt = DateTime.MinValue;
        private const int ToastPerMinute = 3;

        /// <summary>
        /// 用系统默认方式打开一个**已校验**的目标：
        ///   1) http:// 或 https:// 网址；
        ///   2) ms-settings: / ms- 系统页；
        ///   3) 本地路径 —— 必须存在于宿主缓存的「最近使用」清单里（防任意程序执行）。
        /// 其它一律拒绝并返回 false。
        /// </summary>
        public static bool OpenExternally(string? target)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(target)) return false;
                string t = target!.Trim();
                if (t.IndexOf('"') >= 0 || t.IndexOf('\r') >= 0 || t.IndexOf('\n') >= 0) return false;

                if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return Launch(t);
                }
                if (t.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
                {
                    // ★ 只放行 ms-settings:（Windows 设置页），**不要**放宽成所有 ms-*：
                    //   协议族里还有 ms-appinstaller: / search-ms: / ms-officecmd: 等能拉起外部程序处理器的入口，
                    //   按前缀整族放行等于给插件一条"借系统处理器执行"的路。原来的 char.IsWhiteSpace 也挡不住
                    //   无空格的参数写法（如 ...:/id PCWDiagnostic）。需要更多系统页请在本类里**显式加白名单**。
                    if (t.Any(char.IsWhiteSpace)) return false;
                    return Launch(t);
                }

                // 本地路径：必须在最近使用清单里出现（路径规范化后比较）
                string full;
                // 路径非法 = 计划内拒绝（外来代码可以随便传字符串）：这里**不记日志**，否则插件能靠刷非法输入刷爆宿主日志
                try { full = Path.GetFullPath(t); } catch { return false; }
                if (!IsKnownTarget(full)) return false;
                return Launch(full);
            }
            catch (Exception ex)
            {
                // 走到这里 = 宿主自己出问题（白名单校验已在上面做完），留痕便于排查
                LogManager.Warning($"[能力] 打开目标失败（已按白名单拒绝）：{ex.Message}");
                return false;
            }
        }

        private static bool Launch(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                // 打不开（目标失效 / 被组策略或杀软拦）→ 给插件 false，同时留痕
                LogManager.Warning($"[能力] 无法打开目标 {target}：{ex.Message}");
                return false;
            }
        }

        private static bool IsKnownTarget(string fullPath)
        {
            lock (_gate)
            {
                if ((DateTime.UtcNow - _knownTargetsAt).TotalSeconds > 5)
                {
                    var set = new List<string>();
                    try
                    {
                        foreach (var a in RecentAppTracker.GetRecentApps(60))
                            if (!string.IsNullOrEmpty(a.Path)) set.Add(a.Path);
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warning($"[能力] 读取「最近使用」应用清单失败（本次按空清单处理）：{ex.Message}");
                    }
                    try
                    {
                        foreach (var f in GetRecentFiles(60))
                            if (!string.IsNullOrEmpty(f.Target)) set.Add(f.Target);
                    }
                    catch (Exception ex)
                    {
                        LogManager.Warning($"[能力] 读取「最近文件」清单失败（本次按空清单处理）：{ex.Message}");
                    }
                    _knownTargets = set;
                    _knownTargetsAt = DateTime.UtcNow;
                }
                foreach (var p in _knownTargets)
                {
                    // 清单里某条路径非法 → 跳过这一条即可（逐条比较，记日志会刷屏）
                    try { if (string.Equals(Path.GetFullPath(p), fullPath, StringComparison.OrdinalIgnoreCase)) return true; }
                    catch { }
                }
                return false;
            }
        }

        /// <summary>激活一个已存在的窗口（句柄来自宿主窗口列表）。</summary>
        public static bool ActivateWindow(long handle)
        {
            try
            {
                if (handle == 0) return false;
                WindowAction.SwitchTo(new IntPtr(handle));
                return true;
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[能力] 激活窗口失败 handle={handle}：{ex.Message}");
                return false;
            }
        }

        /// <summary>打开 Windows 设置页（仅白名单页面；空 = 设置首页）。</summary>
        public static bool OpenWindowsSettingsPage(string? page = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(page)) { SystemLauncher.OpenWindowsSettings(); return true; }
                string p = page!.Trim().ToLowerInvariant().Replace(" ", "");
                string uri = p switch
                {
                    "bluetooth" => "ms-settings:bluetooth",
                    "wifi" or "network" => "ms-settings:network",
                    "hotspot" => "ms-settings:network-mobilehotspot",
                    "battery" or "powersaver" => "ms-settings:batterysaver",
                    "display" => "ms-settings:display",
                    "sound" => "ms-settings:sound",
                    "apps" => "ms-settings:appsfeatures",
                    "windowsupdate" or "update" => "ms-settings:windowsupdate",
                    _ => "",
                };
                if (uri.Length == 0) return false;
                return Launch(uri);
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[能力] 打开 Windows 设置页失败 page={page}：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 打开宿主设置窗口（可选页签，如 "tabAI"）。
        /// 面板/小组件里的「打开设置」按钮走这里，而不是静态事件 —— 内置件从文件夹加载后类型不再相同，
        /// 静态事件在"文件夹版"上根本不会触发（按钮变哑巴）。
        /// </summary>
        public static void OpenSettingsPage(string? page = null)
        {
            try
            {
                if (System.Windows.Application.Current?.MainWindow is ShoreHue.UI.Main.MainWindow main)
                    main.OpenSettings(page);
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[能力] 打开设置失败 page={page}：{ex.Message}");
            }
        }

        /// <summary>弹一条系统通知（限频：每分钟最多 3 条，防被插件刷屏）。</summary>
        public static bool ShowToast(string? title, string? message)
        {
            try
            {
                lock (_gate)
                {
                    DateTime now = DateTime.UtcNow;
                    while (_toastTimes.Count > 0 && (now - _toastTimes.Peek()).TotalSeconds > 60) _toastTimes.Dequeue();
                    if (_toastTimes.Count >= ToastPerMinute) return false;
                    _toastTimes.Enqueue(now);
                }
                return SystemToast.Show(title ?? "ShoreHue", message ?? "");
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[能力] 弹出通知失败：{ex.Message}");
                return false;
            }
        }

        // ==================== 最近使用 / 网页收藏 ====================

        /// <summary>宿主缓存的「最近使用」清单（应用 + 最近文件 + 网页收藏）。</summary>
        public static List<HostRecentItem> GetRecentItems(int max = 20)
        {
            var list = new List<HostRecentItem>();
            try
            {
                foreach (var a in RecentAppTracker.GetRecentApps(max))
                {
                    if (string.IsNullOrEmpty(a.Path)) continue;
                    list.Add(new HostRecentItem { Kind = "App", Name = string.IsNullOrEmpty(a.Name) ? Path.GetFileNameWithoutExtension(a.Path) : a.Name, Target = a.Path });
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[能力] 读取「最近使用」应用清单失败（结果里缺应用）：{ex.Message}");
            }
            try { list.AddRange(GetRecentFiles(max)); }
            catch (Exception ex)
            {
                LogManager.Warning($"[能力] 读取「最近文件」清单失败（结果里缺文件）：{ex.Message}");
            }
            try
            {
                foreach (var w in WebFavoriteManager.GetCombined(max))
                {
                    if (string.IsNullOrEmpty(w.Url)) continue;
                    list.Add(new HostRecentItem { Kind = "Web", Name = string.IsNullOrEmpty(w.Title) ? WebFavoriteManager.GetDomain(w.Url) : w.Title, Target = w.Url });
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[能力] 读取网页收藏失败（结果里缺网页）：{ex.Message}");
            }
            return list.Take(Math.Max(1, max)).ToList();
        }

        /// <summary>仅网页收藏。</summary>
        public static List<HostRecentItem> GetWebFavorites(int max = 20)
        {
            var list = new List<HostRecentItem>();
            try
            {
                foreach (var w in WebFavoriteManager.GetCombined(max))
                {
                    if (string.IsNullOrEmpty(w.Url)) continue;
                    list.Add(new HostRecentItem { Kind = "Web", Name = string.IsNullOrEmpty(w.Title) ? WebFavoriteManager.GetDomain(w.Url) : w.Title, Target = w.Url });
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[能力] 读取网页收藏失败（返回空列表）：{ex.Message}");
            }
            return list;
        }

        public static bool AddWebFavorite(string url)
        {
            try { return WebFavoriteManager.AddFavorite(url); } catch { return false; }
        }

        public static bool RemoveWebFavorite(string url)
        {
            try { WebFavoriteManager.RemoveFavorite(url); return true; } catch { return false; }
        }

        public static void RecordWebOpen(string url, string? title = null)
        {
            try { WebFavoriteManager.RecordOpen(url, title); }
            catch (Exception ex) { LogManager.Warning($"[能力] 记录网页访问失败 {url}：{ex.Message}"); }
        }

        /// <summary>记录一次启动（只刷新清单里已有的条目）。</summary>
        public static void RecordLaunch(string path)
        {
            try
            {
                // ★ 关键：`addIfMissing: false` —— 最近使用清单同时是 OpenExternally 的路径白名单，
                //   若允许这里新增条目，插件就能"先记录 cmd.exe、再打开它"，把窄接口变成任意程序执行。
                RecentAppTracker.RecordLaunch(path, addIfMissing: false);
            }
            catch (Exception ex) { LogManager.Warning($"[能力] 记录启动失败 {path}：{ex.Message}"); }
        }

        private static List<HostRecentItem> GetRecentFiles(int max)
        {
            var list = new List<HostRecentItem>();
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;
            foreach (var lnk in Directory.GetFiles(dir, "*.lnk")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Take(Math.Max(1, max)))
            {
                try
                {
                    string target = ShortcutLinkResolver.Resolve(lnk);
                    if (string.IsNullOrEmpty(target) || !File.Exists(target)) continue;
                    list.Add(new HostRecentItem { Kind = "File", Name = Path.GetFileNameWithoutExtension(lnk), Target = target });
                }
                catch (Exception ex)
                {
                    // 坏快捷方式很常见（目标已删/权限不足）→ 跳过这一条，Debug 留痕即可
                    LogManager.Debug($"[能力] 跳过无法解析的快捷方式 {lnk}：{ex.Message}");
                }
            }
            return list;
        }

        /// <summary>把纯文本写进剪贴板（返回是否成功）。
        /// ★ 为什么要包这一层：宿主剪贴板接口的写入方法，其参数类型是「宿主剪贴板管理器」里的条目类型，
        ///   而那个类型在符号层黑名单里 —— 插件只要**写出它的类型名**就会被判违规、整包被拦，
        ///   哪怕它只是想往剪贴板里放一段文字。能力本身没有变化（剪贴板写入对插件一直是开放的），
        ///   这里只是给外来代码一个不引用宿主内部类型的入口。</summary>
        public static bool CopyToClipboard(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                var svc = ClipboardHistory;
                if (svc == null)
                {
                    LogManager.Warning("[能力] 写剪贴板失败：宿主剪贴板服务不可用");
                    return false;
                }
                svc.CopyToClipboard(ShoreHue.Core.Services.ClipboardManager.ClipboardItem.FromText(text));
                return true;
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[能力] 写剪贴板失败：{ex.Message}");
                return false;
            }
        }

        // ==================== 划词 / AI（宿主侧执行，密钥不出宿主）====================

        /// <summary>读取当前选中文本（宿主实现；必须在 UI 线程调用）。失败返回空串。</summary>
        public static async Task<string> CaptureSelectedTextAsync()
        {
            try
            {
                var r = await SelectedTextCapture.CaptureAsync().ConfigureAwait(true);
                return r?.Text ?? "";
            }
            catch (Exception ex)
            {
                // 划词失败按"没选到"处理（返回空串），但留痕：宿主抓取链路坏了要能看出来
                LogManager.Warning($"[能力] 划词捕获失败（按未选中处理）：{ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// 调用用户配置的 AI（OpenAI 兼容）。密钥由宿主解密使用，不交给插件。
        /// onDelta 为流式增量回调（UI 线程）；返回完整回复。未配置/失败时抛异常由调用方提示。
        /// </summary>
        public static async Task<string> AskAiAsync(string prompt, Action<string>? onDelta = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("prompt 为空", nameof(prompt));
            if (prompt.Length > 8000) prompt = prompt.Substring(0, 8000);   // 限制单次输入长度
            var settings = AiSettingsStore.Load();
            if (settings == null || !settings.Enabled || string.IsNullOrWhiteSpace(settings.ApiKey))
                throw new InvalidOperationException("尚未在「设置 → AI」中启用并填写服务商与密钥");

            var history = new List<ShoreHue.Core.Services.Ai.ChatMessage>();
            using var client = new ShoreHue.Core.Services.Ai.AiChatClient();
            return await client.StreamChatAsync(settings, history, prompt, d => onDelta?.Invoke(d), ct)
                               .ConfigureAwait(true);
        }

        // ==================== 通知坞 ====================

        /// <summary>系统通知集合（宿主已聚合）。订阅 <see cref="NotificationsChanged"/> 获取更新。</summary>
        public static System.Collections.ObjectModel.ObservableCollection<ToastNotificationItem> Notifications
            => ToastMonitor.Notifications;

        public static event Action? NotificationsChanged
        {
            add { ToastMonitor.Changed += value; }
            remove { ToastMonitor.Changed -= value; }
        }

        /// <summary>打开通知来源应用。</summary>
        public static void OpenNotification(ToastNotificationItem? item)
        {
            if (item == null) return;
            try { ToastMonitor.OpenApp(item); }
            catch (Exception ex) { LogManager.Warning($"[能力] 打开通知来源应用失败：{ex.Message}"); }
        }

        /// <summary>从通知坞移除一条。</summary>
        public static void RemoveNotification(ToastNotificationItem? item)
        {
            if (item == null) return;
            try { ToastMonitor.RemoveItem(item); }
            catch (Exception ex) { LogManager.Warning($"[能力] 移除通知失败：{ex.Message}"); }
        }

        public static void ClearNotifications()
        {
            try { ToastMonitor.ClearAll(); }
            catch (Exception ex) { LogManager.Warning($"[能力] 清空通知失败：{ex.Message}"); }
        }

        /// <summary>通知坞面板（宿主视图；插件可直接挂载）。</summary>
        public static UserControl CreateNotificationDockPanel() => new ShoreHue.UI.Panels.NotificationDockView();

        // ==================== 宿主服务（只给接口，不给服务容器）====================

        public static ISettingsService? Settings
            => (ServiceManager.Instance.GetService<SettingsManager>() as ISettingsService)
               ?? ServiceManager.Instance.All.OfType<ISettingsService>().FirstOrDefault();

        public static IClipboardService? ClipboardHistory
            => (ServiceManager.Instance.GetService<ClipboardManager>() as IClipboardService)
               ?? ServiceManager.Instance.All.OfType<IClipboardService>().FirstOrDefault();

        public static INoteService? Notes
            => (ServiceManager.Instance.GetService<NoteManager>() as INoteService)
               ?? ServiceManager.Instance.All.OfType<INoteService>().FirstOrDefault();

        public static IShortcutService? Shortcuts
            => ServiceManager.Instance.GetService<ShortcutManager>() as IShortcutService;
    }
}
