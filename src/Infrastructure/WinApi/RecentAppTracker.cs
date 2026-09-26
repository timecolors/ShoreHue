using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Infrastructure.Utils;

namespace ShoreHue.Infrastructure.WinApi
{
    /// <summary>
    /// 最近打开的应用追踪：
    ///  - 周期性地把“当前前台窗口”所属进程记录为最近使用（系统级，无需钩子）；
    ///  - 面板内主动启动应用时也会立即记录（RecordLaunch）。
    /// 数据保存在 data/recent_apps.json，供“最近使用-程序”页展示。
    /// </summary>
    public static class RecentAppTracker
    {
        public sealed class RecentApp
        {
            public string Path { get; set; } = "";
            public string Name { get; set; } = "";
            public DateTime LastUsed { get; set; } = DateTime.Now;
        }

        private static readonly string StorePath = AppPaths.RecentAppsPath;

        private static readonly object _lock = new();
        private static readonly Dictionary<string, RecentApp> _apps = new(StringComparer.OrdinalIgnoreCase);
        private static DispatcherTimer? _timer;
        private static string? _lastForegroundExe;
        private static int _currentPid = Environment.ProcessId;

        public static void Start()
        {
            if (_timer != null) return;

            Load();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _timer.Tick += (_, _) => ScanForeground();
            _timer.Start();
        }

        public static void Stop()
        {
            _timer?.Stop();
            _timer = null;
        }

        /// <summary>
        /// 记录一次启动（更新最近使用的时间/顺序）。
        /// ★ `addIfMissing=false` 时**只刷新已存在的条目，不新增** —— 这是给"外来代码"那条路用的：
        ///   最近使用清单同时是 `HostCapabilities.OpenExternally` 的**路径白名单**，若允许插件新增条目，
        ///   插件就能先记录 `cmd.exe`、等缓存刷新再打开它，等于"自己给自己发白名单"（自我授权）。
        /// </summary>
        public static void RecordLaunch(string path, bool addIfMissing = true)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string exe = path;
            try
            {
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string? resolved = ShortcutLinkResolver.Resolve(path);
                    if (!string.IsNullOrEmpty(resolved)) exe = resolved;
                }
            }
            catch (Exception ex)
            {
                // 尽力而为：.lnk 解析不出来就用原路径记（名字略差，功能不受影响）
                LogManager.Debug($"[最近使用] 解析快捷方式失败（按原路径记录）：{ex.Message}");
            }

            lock (_lock)
            {
                // 不允许新增时：清单里没有这条就什么都不做（保持"白名单只由宿主产生"）
                if (!addIfMissing && !_apps.ContainsKey(exe)) return;
                string name = FriendlyName(exe);
                _apps[exe] = new RecentApp { Path = exe, Name = name, LastUsed = DateTime.Now };
                Trim();
                Save();
            }
        }

        public static IReadOnlyList<RecentApp> GetRecentApps(int max = 30)
        {
            lock (_lock)
            {
                return _apps.Values
                    .OrderByDescending(a => a.LastUsed)
                    .Take(max)
                    .ToList();
            }
        }

        // ================= 前台窗口扫描 =================

        private static void ScanForeground()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || pid == (uint)_currentPid) return;

                using var proc = Process.GetProcessById((int)pid);
                string? exe = proc.MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return;
                if (IsNoise(exe)) return;

                // 桌面/任务栏/系统托盘噪音
                string name = Path.GetFileNameWithoutExtension(exe);
                if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
                    proc.MainWindowTitle.Length == 0)
                {
                    return;
                }

                if (string.Equals(exe, _lastForegroundExe, StringComparison.OrdinalIgnoreCase)) return;
                _lastForegroundExe = exe;

                lock (_lock)
                {
                    _apps[exe] = new RecentApp { Path = exe, Name = FriendlyName(exe), LastUsed = DateTime.Now };
                    Trim();
                    Save();
                }
            }
            catch (Exception ex)
            {
                // 用户刚启动的程序没进"最近使用"（界面可见的功能缺失）
                LogManager.Warning($"[最近使用] 记录程序启动失败（列表不会更新）：{ex.Message}");
            }
        }

        private static string FriendlyName(string exe)
        {
            string baseName = Path.GetFileNameWithoutExtension(exe);
            return baseName.ToLowerInvariant() switch
            {
                "qq" or "qqnt" => "QQ",
                "wechat" or "weixin" => ShoreHue.UI.Localization.LocalizationManager.Instance["Recent_Wechat"],
                "chrome" => "Chrome",
                "msedge" => "Edge",
                "devenv" => "Visual Studio",
                "explorer" => ShoreHue.UI.Localization.LocalizationManager.Instance["Recent_Explorer"],
                _ => baseName
            };
        }

        /// <summary>
        /// 过滤明显不适用于“最近打开的应用”的噪音（驱动安装器、系统组件、临时程序）。
        /// </summary>
        private static bool IsNoise(string exe)
        {
            try
            {
                string p = exe.ToLowerInvariant();
                if (p.Contains("\\drivers\\") ||
                    p.Contains("\\sysdiag\\bin\\") ||
                    p.Contains("\\installer\\") ||
                    p.Contains("\\temp\\") ||
                    p.StartsWith(@"c:\windows\") ||
                    p.StartsWith(@"c:\program files\windows"))
                {
                    return true;
                }

                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                return name.StartsWith("setup") ||
                       name.StartsWith("install") ||
                       name.StartsWith("unins") ||
                       name.Contains("cleanup");
            }
            catch (Exception ex)
            {
                // 判不出来就当"不是安装程序"（最坏是多记一条到列表里）
                LogManager.Debug($"[最近使用] 判断安装程序名失败（按普通程序处理）：{ex.Message}");
                return false;
            }
        }

        private static void Trim()
        {
            while (_apps.Count > 40)
            {
                var oldest = _apps.Values.OrderBy(a => a.LastUsed).FirstOrDefault();
                if (oldest == null) break;
                _apps.Remove(oldest.Path);
            }
        }

        // ================= 持久化 =================

        private static void Load()
        {
            try
            {
                lock (_lock)
                {
                    if (File.Exists(StorePath))
                    {
                        var list = JsonSerializer.Deserialize<List<RecentApp>>(File.ReadAllText(StorePath));
                        if (list != null)
                        {
                            _apps.Clear();
                            foreach (var item in list)
                            {
                                if (!string.IsNullOrEmpty(item.Path))
                                {
                                    _apps[item.Path] = item;
                                }
                            }
                        }
                    }

                    if (_apps.Count == 0)
                    {
                        SeedFromUserAssist();
                        Save();
                    }
                }
            }
            catch (Exception ex)
            {
                // 首次播种失败 → 最近使用列表一开始是空的（用户会以为功能没生效）
                LogManager.Warning($"[最近使用] 首次播种失败（列表可能为空）：{ex.Message}");
            }
        }

        /// <summary>
        /// 首次运行时用 UserAssist（系统记录过的启动项）做初始种子，
        /// 让“最近打开的应用”页一开始就有内容；之后由前台窗口扫描持续更新。
        /// </summary>
        private static void SeedFromUserAssist()
        {
            try
            {
                const string userAssistRoot =
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
                using var root = Registry.CurrentUser.OpenSubKey(userAssistRoot);
                if (root == null) return;

                var entries = new List<(string Path, DateTime Time, int Order)>();
                int fallbackOrder = 0;

                foreach (var guidName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var countKey = root.OpenSubKey(guidName + @"\Count");
                        if (countKey == null) continue;

                        foreach (var valueName in countKey.GetValueNames())
                        {
                            if (valueName.StartsWith("UEME_", StringComparison.OrdinalIgnoreCase)) continue;
                            if (countKey.GetValue(valueName) is not byte[] data || data.Length < 16) continue;

                            string decoded = Rot13(valueName);
                            if (string.IsNullOrWhiteSpace(decoded)) continue;

                            string exe = ResolveToExe(decoded);
                            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) continue;
                            if (IsNoise(exe)) continue;
                            if (_apps.ContainsKey(exe)) continue;

                            // ★ "上次执行时间"的偏移量取决于记录结构，**不能一律取 offset 4**：
                            //   · Win7+ 的 72 字节记录：0x00 session / 0x04 运行次数 / 0x08 焦点次数 /
                            //     0x0C 焦点时长 → FILETIME 在 0x3C(60)
                            //   · XP 时代的 16 字节记录：0x00 session / 0x04 运行次数 → FILETIME 在 0x08(8)
                            //   旧实现固定读 offset 4：在新结构里那是"运行次数"（个位数/小整数），
                            //   被当成 FILETIME 解释出来是 1601 年附近 → 整张"最近使用"列表的排序完全失真
                            //   （所有真实条目都排到了保底时间戳后面）。
                            long fileTime = data.Length >= 72 ? BitConverter.ToInt64(data, 60)
                                          : data.Length >= 16 ? BitConverter.ToInt64(data, 8)
                                          : 0;
                            DateTime? parsed = TryFromFileTime(fileTime);
                            DateTime time = parsed ?? DateTime.Now.AddMinutes(-fallbackOrder);
                            fallbackOrder += 3;

                            entries.Add((exe, time, fallbackOrder));
                        }
                    }
                    catch (Exception ex)
                    {
                        // 尽力而为：某一条注册表项读不出来就跳过（UserAssist 里本来就有各种异常项）
                        LogManager.Debug($"[最近使用] 跳过无法读取的 UserAssist 项：{ex.Message}");
                    }
                }

                foreach (var e in entries.OrderByDescending(e => e.Time).Take(25))
                {
                    if (!_apps.ContainsKey(e.Path))
                    {
                        _apps[e.Path] = new RecentApp
                        {
                            Path = e.Path,
                            Name = FriendlyName(e.Path),
                            LastUsed = e.Time
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[最近使用] 读取 UserAssist 失败（本次没有历史程序）：{ex.Message}");
            }
        }

        /// <summary>
        /// 把可能被误读的 FILETIME 转成 DateTime；不在合理范围内就返回 null（调用方走保底时间）。
        /// ★ 必须校验：注册表里这个字段可能是被误取的小整数、0、或垃圾值，
        ///   `DateTime.FromFileTime` 对越界值会抛 ArgumentOutOfRangeException（外面有 catch，
        ///   但那条 catch 会把"整个 UserAssist 分支"当成读取失败跳过，静默丢掉全部条目）。
        /// </summary>
        private static DateTime? TryFromFileTime(long fileTime)
        {
            try
            {
                if (fileTime <= 0) return null;
                var t = DateTime.FromFileTime(fileTime);
                // 合理区间：2000-01-01 ~ 现在+1天（UserAssist 只会记录过去发生过的执行）
                var min = new DateTime(2000, 1, 1);
                var max = DateTime.Now.AddDays(1);
                return t >= min && t <= max ? t : null;
            }
            catch (Exception ex)
            {
                // 越界/非法 FILETIME：按"没有时间"处理，交由保底顺序（留痕便于排查）
                LogManager.Debug($"[最近使用] UserAssist 时间戳不可用（按保底顺序处理）：{ex.Message}");
                return null;
            }
        }

        private static string Rot13(string s)
        {
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c >= 'A' && c <= 'Z')
                    chars[i] = (char)('A' + (c - 'A' + 13) % 26);
                else if (c >= 'a' && c <= 'z')
                    chars[i] = (char)('a' + (c - 'a' + 13) % 26);
            }
            return new string(chars);
        }

        private static string ResolveToExe(string decoded)
        {
            try
            {
                if (decoded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // 形如 C:\...\app.exe
                    return decoded.Contains(":\\") ? decoded : "";
                }
                if (decoded.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    // 形如 {GUID}\Path\App.lnk 或 C:\...\App.lnk
                    string? resolved = ShortcutLinkResolver.Resolve(decoded);
                    if (!string.IsNullOrEmpty(resolved) &&
                        resolved.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return resolved;
                    }
                }
            }
            catch (Exception ex)
            {
                // 尽力而为：解析不出就返回空串，调用方按"没有目标"处理
                LogManager.Debug($"[最近使用] 解析快捷方式目标失败（返回空）：{ex.Message}");
            }
            return "";
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                File.WriteAllText(StorePath, JsonSerializer.Serialize(_apps.Values.ToList()));
            }
            catch (Exception ex)
            {
                // 没落盘 = 这次记录的最近使用重启后全丢（数据持久化失败）
                LogManager.Warning($"[最近使用] 保存记录失败（重启后本次记录会丢失）：{ex.Message}");
            }
        }

        // ================= Win32 =================

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    }
}
