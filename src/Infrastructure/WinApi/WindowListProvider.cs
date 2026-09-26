using System;
using System.Collections.Generic;
using ShoreHue.Core.Infrastructure.Logging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShoreHue.Infrastructure.WinApi
{
    public static class WindowListProvider
    {
        // ========== Win32 常量 ==========
        private const int GWL_EXSTYLE = -20;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_APPWINDOW = 0x00040000;

        // ========== 窗口项类 ==========
        public class WindowItem
        {
            public IntPtr Handle { get; set; }
            public string Title { get; set; } = "";
            /// <summary>所属进程的可执行文件路径（供“固定到任务栏”使用）。</summary>
            public string ProcessPath { get; set; } = "";
            public ImageSource? Icon { get; set; }
            public string ClassName { get; set; } = "";
            public bool IsVisible { get; set; }
            public bool IsToolWindow { get; set; }
            public bool HasTaskbarButton { get; set; }
        }

        // ========== 过滤模式 ==========
        public enum WindowFilterMode
        {
            All,           // 所有可见顶层窗口
            UserAppsOnly   // 仅任务栏显示的应用
        }

        // ========== 公开方法 ==========
        public static List<WindowItem> GetOpenWindows(WindowFilterMode filterMode = WindowFilterMode.UserAppsOnly)
        {
            var windows = new List<WindowItem>();
            var callback = new EnumWindowsProc((hwnd, lParam) =>
            {
                try
                {
                    // 基本过滤：可见 + 有标题
                    if (!IsWindowVisible(hwnd)) return true;
                    if (GetWindowTextLength(hwnd) == 0) return true;

                    string title = GetWindowText(hwnd);
                    if (string.IsNullOrWhiteSpace(title)) return true;

                    string className = GetClassName(hwnd);

                    // 排除系统桌面窗口
                    if (className == "Progman" ||
                        className == "Shell_TrayWnd" ||
                        className == "WorkerW")
                        return true;

                    // ★★★ 核心：判断是否显示在任务栏 ★★★
                    bool showsInTaskbar = ShouldShowInTaskbar(hwnd);

                    var item = new WindowItem
                    {
                        Handle = hwnd,
                        Title = title,
                        ClassName = className,
                        IsVisible = true,
                        IsToolWindow = (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0,
                        HasTaskbarButton = showsInTaskbar
                    };

                    // 获取进程路径（带缓存，供拖拽固定使用）
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    item.ProcessPath = GetProcessPath(pid);
                    // 获取图标（按进程缓存，避免每秒刷新全量重复提取）
                    item.Icon = GetWindowIcon(hwnd, pid);

                    windows.Add(item);
                }
                catch
                {
                    // 尽力而为且**刻意不记日志**：这里在枚举回调里、每次刷新都会走一遍，
                    //   单个窗口读失败只影响它自己（少一个条目），下一轮刷新会重试 —— 记日志会刷屏
                }
                return true;
            });

            EnumWindows(callback, IntPtr.Zero);

            // 应用过滤
            if (filterMode == WindowFilterMode.UserAppsOnly)
            {
                windows = windows.Where(w => w.HasTaskbarButton).ToList();
            }

            return windows;
        }

        /// <summary>按窗口句柄获取所属进程的可执行文件路径。</summary>
        public static string GetProcessPathByHandle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            GetWindowThreadProcessId(hwnd, out uint pid);
            return GetProcessPath(pid);
        }

        /// <summary>按窗口句柄获取所属进程 Id；失败返回 0。</summary>
        public static uint GetProcessId(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid;
        }

        private static readonly Dictionary<uint, string> _processPathCache = new();

        private static string GetProcessPath(uint pid)
        {
            if (pid == 0) return "";
            if (_processPathCache.TryGetValue(pid, out var cached)) return cached;

            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                string? file = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(file))
                {
                    _processPathCache[pid] = file.Trim();
                    return file.Trim();
                }
            }
            catch (Exception ex)
            {
                // 尽力而为：查不到进程路径（提权/受保护进程）→ 下面的缓存空串，不会反复查（每个 pid 只失败一次）
                LogManager.Debug($"[窗口列表] 读取进程路径失败 pid={pid}：{ex.Message}");
            }

            _processPathCache[pid] = "";
            return "";
        }

        /// <summary>
        /// 按进程可执行文件路径查找其主窗口（用于“最近打开的应用”点击时直接激活已有窗口）。
        /// </summary>
        public static IntPtr? FindWindowByProcessPath(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            string target = exePath.Trim();

            IntPtr? found = null;
            var callback = new EnumWindowsProc((hwnd, lParam) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    if (GetWindowTextLength(hwnd) == 0) return true;

                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == 0) return true;

                    using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                    string? file = proc.MainModule?.FileName;
                    if (string.IsNullOrEmpty(file)) return true;

                    if (string.Equals(file.Trim(), target, StringComparison.OrdinalIgnoreCase))
                    {
                        found = hwnd;
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    // 尽力而为：某个窗口的属性读不出来就跳过它，继续找下一个
                    LogManager.Debug($"[窗口列表] 遍历窗口时读取属性失败（已跳过该窗口）：{ex.Message}");
                }
                return true;
            });

            EnumWindows(callback, IntPtr.Zero);
            return found;
        }

        /// <summary>
        /// 单次枚举所有可见窗口，建立“进程 exe 路径 → 主窗口句柄”映射（每个 exe 取第一个）。
        /// 用于最近使用列表批量匹配，避免逐项全量枚举。
        /// </summary>
        public static void EnumerateWindowExeHandles(Dictionary<string, IntPtr> result)
        {
            if (result == null) return;
            var callback = new EnumWindowsProc((hwnd, lParam) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    if (GetWindowTextLength(hwnd) == 0) return true;

                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == 0) return true;

                    using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                    string? file = proc.MainModule?.FileName;
                    if (string.IsNullOrEmpty(file)) return true;

                    if (!result.ContainsKey(file.Trim()))
                    {
                        result[file.Trim()] = hwnd;
                    }
                }
                catch (Exception ex)
                {
                    // 尽力而为：这个窗口不进映射（最坏是它不在任务栏里显示）
                    LogManager.Debug($"[窗口列表] 建立进程→窗口映射时失败（已跳过该窗口）：{ex.Message}");
                }
                return true;
            });

            EnumWindows(callback, IntPtr.Zero);
        }

        // ============================================================
        //  ★★★ 核心方法：判断窗口是否应该出现在任务栏 ★★★
        //  基于 Windows 官方规则：
        //  1. WS_EX_APPWINDOW → 强制出现
        //  2. WS_EX_TOOLWINDOW → 强制隐藏
        //  3. 有所有者窗口 → 隐藏（由所有者管理）
        //  4. 无所有者窗口 → 出现
        // ============================================================
        private static bool ShouldShowInTaskbar(IntPtr hwnd)
        {
            // 获取扩展样式
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

            // 规则 1：WS_EX_APPWINDOW → 强制出现在任务栏
            if ((exStyle & WS_EX_APPWINDOW) != 0)
                return true;

            // 规则 2：WS_EX_TOOLWINDOW → 强制隐藏
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                return false;

            // 规则 3 & 4：检查是否有所有者窗口
            IntPtr owner = GetWindow(hwnd, GW_OWNER);

            // 有所有者 → 隐藏（由所有者管理）
            if (owner != IntPtr.Zero)
                return false;

            // 无所有者 → 出现在任务栏
            return true;
        }

        // ============================================================
        //  Win32 API 声明
        // ============================================================

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, ref RECT rect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private const uint GW_OWNER = 4;
        private const uint GA_ROOT = 2;

        private const int WM_GETICON = 0x007F;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const int GCL_HICON = -14;
        private const int GCL_HICONSM = -34;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        // ============================================================
        //  辅助方法
        // ============================================================

        private static string GetWindowText(IntPtr hWnd)
        {
            const int nChars = 256;
            var buff = new System.Text.StringBuilder(nChars);
            return GetWindowText(hWnd, buff, nChars) > 0 ? buff.ToString() : "";
        }

        private static string GetClassName(IntPtr hWnd)
        {
            const int nChars = 256;
            var buff = new System.Text.StringBuilder(nChars);
            return GetClassName(hWnd, buff, nChars) > 0 ? buff.ToString() : "";
        }

        private static readonly Dictionary<uint, ImageSource> _windowIconCache = new();

        private static ImageSource? GetWindowIcon(IntPtr hwnd, uint pid)
        {
            if (pid != 0 && _windowIconCache.TryGetValue(pid, out var cached)) return cached;
            try
            {
                IntPtr iconHandle = SendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL, IntPtr.Zero);
                if (iconHandle == IntPtr.Zero)
                    iconHandle = SendMessage(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
                if (iconHandle == IntPtr.Zero)
                    iconHandle = GetClassLong(hwnd, GCL_HICONSM);
                if (iconHandle == IntPtr.Zero)
                    iconHandle = GetClassLong(hwnd, GCL_HICON);

                if (iconHandle != IntPtr.Zero)
                {
                    using (var icon = System.Drawing.Icon.FromHandle(iconHandle))
                    {
                        var source = Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle,
                            new Int32Rect(0, 0, icon.Width, icon.Height),
                            BitmapSizeOptions.FromEmptyOptions());
                        source.Freeze();
                        if (pid != 0)
                        {
                            if (_windowIconCache.Count > 400) _windowIconCache.Clear();
                            _windowIconCache[pid] = source;
                        }
                        return source;
                    }
                }
            }
            catch (Exception ex)
            {
                // 尽力而为（UI 装饰）：取不到图标就显示占位（结果按 pid 缓存，不会反复失败）
                LogManager.Debug($"[窗口列表] 提取窗口图标失败 pid={pid}：{ex.Message}");
            }
            return null;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetClassLong(IntPtr hWnd, int nIndex);
    }
}
