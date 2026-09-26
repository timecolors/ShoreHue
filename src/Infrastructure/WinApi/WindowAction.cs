using System;
using System.Runtime.InteropServices;
using ShoreHue.Core.Infrastructure.Logging;

namespace ShoreHue.Infrastructure.WinApi
{
    public static class WindowAction
    {
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;
        private const int SW_MINIMIZE = 6;
        private const int SW_MAXIMIZE = 3;
        private const int WM_CLOSE = 0x0010;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MINIMIZE = 0xF020;
        private const int SC_RESTORE = 0xF120;
        private const int SC_MAXIMIZE = 0xF030;
        private const uint GA_ROOT = 2;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new(-2);

        public static void SwitchTo(IntPtr hwnd)
        {
            try
            {
                if (IsIconic(hwnd))
                    ShowWindow(hwnd, SW_RESTORE);
                ShowWindow(hwnd, SW_SHOW);
                SetForegroundWindow(hwnd);
                BringWindowToTop(hwnd);
                SwitchToThisWindow(hwnd, true);
            }
            catch (Exception ex)
            {
                // 尽力而为：Win32 调用对"目标窗口已关闭/被 UIPI 拦（提权窗口）"只会失败或抛异常，
                //   用户看到的现象就是"点了没反应"，记 Debug 便于排查（每次都是用户点击触发，不会刷屏）
                LogManager.Debug($"[窗口动作] 切换到窗口失败：{ex.Message}");
            }
        }

        public static void Close(IntPtr hwnd)
        {
            // 尽力而为：关不掉的常见原因是目标窗口提权（UIPI 拦跨权限消息）
            try { SendMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception ex) { LogManager.Debug($"[窗口动作] 发送关闭消息失败：{ex.Message}"); }
        }

        public static bool IsWindowAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

        public static void Restore(IntPtr hwnd)
        {
            try
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                BringWindowToTop(hwnd);
                SwitchToThisWindow(hwnd, true);
            }
            catch (Exception ex)
            {
                // 尽力而为：Win32 调用对"目标窗口已关闭/被 UIPI 拦（提权窗口）"只会失败或抛异常，
                //   用户看到的现象就是"点了没反应"，记 Debug 便于排查（每次都是用户点击触发，不会刷屏）
                LogManager.Debug($"[窗口动作] 激活并前置窗口失败：{ex.Message}");
            }
        }

        public static void ToggleMinimize(IntPtr hwnd)
        {
            try
            {
                if (IsIconic(hwnd))
                {
                    SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_RESTORE, IntPtr.Zero);
                    SetForegroundWindow(hwnd);
                    SwitchToThisWindow(hwnd, true);
                }
                else
                {
                    SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MINIMIZE, IntPtr.Zero);
                }
            }
            catch (Exception ex)
            {
                // 尽力而为：Win32 调用对"目标窗口已关闭/被 UIPI 拦（提权窗口）"只会失败或抛异常，
                //   用户看到的现象就是"点了没反应"，记 Debug 便于排查（每次都是用户点击触发，不会刷屏）
                LogManager.Debug($"[窗口动作] 切换最小化失败：{ex.Message}");
            }
        }

        public static IntPtr GetRootWindow(IntPtr hwnd)
        {
            try
            {
                return GetAncestor(hwnd, GA_ROOT);
            }
            catch
            {
                IntPtr top = hwnd;
                while (true)
                {
                    IntPtr parent = GetParent(top);
                    if (parent == IntPtr.Zero) break;
                    top = parent;
                }
                return top;
            }
        }

        public static IntPtr GetForegroundRootWindow()
        {
            IntPtr foreground = GetForegroundWindow();
            return GetRootWindow(foreground);
        }

        public static bool IsCurrentWindow(IntPtr hwnd)
        {
            IntPtr hwndRoot = GetRootWindow(hwnd);
            IntPtr foregroundRoot = GetForegroundRootWindow();
            return hwndRoot == foregroundRoot;
        }

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        /// <summary>最大化/还原切换。</summary>
        public static void ToggleMaximize(IntPtr hwnd)
        {
            try
            {
                if (IsZoomed(hwnd))
                    ShowWindow(hwnd, SW_RESTORE);
                else
                    ShowWindow(hwnd, SW_MAXIMIZE);
            }
            catch (Exception ex)
            {
                // 尽力而为：Win32 调用对"目标窗口已关闭/被 UIPI 拦（提权窗口）"只会失败或抛异常，
                //   用户看到的现象就是"点了没反应"，记 Debug 便于排查（每次都是用户点击触发，不会刷屏）
                LogManager.Debug($"[窗口动作] 切换最大化失败：{ex.Message}");
            }
        }

        /// <summary>窗口置顶/取消置顶切换。</summary>
        public static void ToggleTopmost(IntPtr hwnd)
        {
            try
            {
                // 查询当前是否置顶（GetWindowLong 的 WS_EX_TOPMOST 位）
                long ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                bool isTopmost = (ex & WS_EX_TOPMOST) != 0;
                SetWindowPos(hwnd, isTopmost ? HWND_NOTOPMOST : HWND_TOPMOST,
                    0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                // 尽力而为：Win32 调用对"目标窗口已关闭/被 UIPI 拦（提权窗口）"只会失败或抛异常，
                //   用户看到的现象就是"点了没反应"，记 Debug 便于排查（每次都是用户点击触发，不会刷屏）
                LogManager.Debug($"[窗口动作] 切换置顶失败：{ex.Message}");
            }
        }

        /// <summary>移动并调整窗口大小（屏幕物理坐标）。</summary>
        public static void MoveResize(IntPtr hwnd, int x, int y, int width, int height)
        {
            try
            {
                SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                // 尽力而为：Win32 调用对"目标窗口已关闭/被 UIPI 拦（提权窗口）"只会失败或抛异常，
                //   用户看到的现象就是"点了没反应"，记 Debug 便于排查（每次都是用户点击触发，不会刷屏）
                LogManager.Debug($"[窗口动作] 移动/调整窗口失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 显示桌面（最小化所有窗口）：模拟 WIN+D。
        /// 面板自身是 Topmost 且不显示在任务栏，不受影响。
        /// </summary>
        public static void ShowDesktop()
        {
            try
            {
                keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                keybd_event(VK_D, 0, 0, UIntPtr.Zero);
                keybd_event(VK_D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                // 尽力而为：Win32 调用对"目标窗口已关闭/被 UIPI 拦（提权窗口）"只会失败或抛异常，
                //   用户看到的现象就是"点了没反应"，记 Debug 便于排查（每次都是用户点击触发，不会刷屏）
                LogManager.Debug($"[窗口动作] 模拟 Win+D 显示桌面失败：{ex.Message}");
            }
        }

        private const byte VK_LWIN = 0x5B;
        private const byte VK_D = 0x44;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOPMOST = 0x00000008;

        [DllImport("user32.dll")]
        private static extern long GetWindowLong(IntPtr hWnd, int nIndex);
    }
}
