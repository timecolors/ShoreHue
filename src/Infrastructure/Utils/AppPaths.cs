using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ShoreHue.Infrastructure.Utils
{
    /// <summary>
    /// 应用数据路径与打包状态。
    /// - 数据统一放在 %LOCALAPPDATA%\ShoreHue，普通版（zip/单文件）与商店 MSIX 版通用，
    ///   安装目录只读也不会影响写入；
    /// - IsPackaged 用于区分商店包：商店版禁用 GitHub 自更新、开机自启用 StartupTask、
    ///   Toast 走包身份（无需手工创建快捷方式）。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>测试/探针注入的数据根目录（隔离，不污染真实用户数据）；null = 用默认 %LOCALAPPDATA%\ShoreHue。</summary>
        internal static string? TestDataRoot;

        public static string DataRoot => TestDataRoot ?? BuildDataRoot();

        public static string LogDirectory => Path.Combine(DataRoot, "Logs");
        public static string ConfigPath => Path.Combine(DataRoot, "config.json");
        public static string RecentAppsPath => Path.Combine(DataRoot, "recent_apps.json");
        public static string FavoritesPath => Path.Combine(DataRoot, "favorite_webs.json");
        public static string RecentWebsPath => Path.Combine(DataRoot, "recent_webs.json");
        public static string ShortcutsPath => Path.Combine(DataRoot, "shortcuts.json");
        public static string NotesPath => Path.Combine(DataRoot, "notes.json");
        public static string ClipboardHistoryPath => Path.Combine(DataRoot, "clipboard_history.json");
        public static string ClipboardCacheDir => Path.Combine(DataRoot, "clipboard_cache");
        public static string SystemToastLogPath => Path.Combine(LogDirectory, "system-toast.log");
        public static string AiSettingsPath => Path.Combine(DataRoot, "ai.json");
        public static string AiHistoryPath => Path.Combine(DataRoot, "ai_history.json");
        public static string AiSessionsPath => Path.Combine(DataRoot, "ai_sessions.json");
        public static string PresetsDir => Path.Combine(DataRoot, "Presets");

        /// <summary>是否运行在 MSIX 打包环境（Microsoft Store 版）。</summary>
        public static bool IsPackaged { get; } = DetectPackaged();

        private static string BuildDataRoot()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(local)
                ? Path.Combine(AppContext.BaseDirectory, "Data")
                : Path.Combine(local, "ShoreHue");
        }

        private static bool DetectPackaged()
        {
            try
            {
                int length = 0;
                int hr = GetCurrentPackageFullName(ref length, null);
                // 15700 = APPMODEL_ERROR_NO_PACKAGE（未打包）
                if (hr == 15700) return false;
                // 122 = ERROR_INSUFFICIENT_BUFFER：有包身份，补一次调用取全名
                if (hr == 122)
                {
                    var sb = new StringBuilder(length);
                    return GetCurrentPackageFullName(ref length, sb) == 0;
                }
                return hr == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 普通版升级迁移：旧版本数据写在安装目录/Data 与安装目录/config.json，
        /// 首次运行新版时搬到 LocalAppData，避免用户配置/快捷方式丢失。
        /// </summary>
        public static void MigrateLegacyData()
        {
            try
            {
                if (Directory.Exists(DataRoot)) return; // 已迁移过
                Directory.CreateDirectory(DataRoot);

                CopyIfExists(Path.Combine(AppContext.BaseDirectory, "config.json"), ConfigPath);

                string legacy = Path.Combine(AppContext.BaseDirectory, "Data");
                if (Directory.Exists(legacy))
                {
                    foreach (var file in Directory.EnumerateFiles(legacy, "*.json"))
                        CopyIfExists(file, Path.Combine(DataRoot, Path.GetFileName(file)));

                    string cache = Path.Combine(legacy, "clipboard_cache");
                    if (Directory.Exists(cache))
                    {
                        Directory.CreateDirectory(ClipboardCacheDir);
                        foreach (var file in Directory.EnumerateFiles(cache))
                            CopyIfExists(file, Path.Combine(ClipboardCacheDir, Path.GetFileName(file)));
                    }

                    string logs = Path.Combine(legacy, "Logs");
                    if (Directory.Exists(logs))
                    {
                        Directory.CreateDirectory(LogDirectory);
                        foreach (var file in Directory.EnumerateFiles(logs))
                            CopyIfExists(file, Path.Combine(LogDirectory, Path.GetFileName(file)));
                    }
                }
            }
            catch
            {
                // 迁移失败不阻塞启动
            }
        }

        private static void CopyIfExists(string src, string dst)
        {
            if (!File.Exists(src) || File.Exists(dst)) return;
            try { File.Copy(src, dst); } catch { /* 尽力而为：旧日志拷不过来不影响启动，也不影响配置 */ }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);
    }
}
