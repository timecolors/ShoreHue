using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ShoreHue.Infrastructure.Utils;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.UI.Localization;

namespace ShoreHue.UI.Widgets
{
    /// <summary>
    /// 右上角"窗口操作中心"：对当前前台窗口执行最小化/最大化/关闭/置顶/贴靠布局。
    /// 弥补右上角安全区不呼出内容面板的缺憾——把这块"危险区"变成窗口管理入口。
    /// </summary>
    public partial class WindowControlView : UserControl
    {
        private readonly DispatcherTimer _titleTimer;

        public WindowControlView()
        {
            InitializeComponent();
            // 标题每秒刷新；若前台是面板自身则显示提示
            _titleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _titleTimer.Tick += (_, _) => RefreshTitle();
            RefreshTitle();
            Loaded += (_, _) => _titleTimer.Start();
            Unloaded += (_, _) => _titleTimer.Stop();
        }

        /// <summary>当前目标窗口（前台窗口，排除本进程）。</summary>
        private IntPtr GetTargetWindow()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            if (WindowListProvider.GetProcessId(hwnd) == Environment.ProcessId) return IntPtr.Zero;
            return hwnd;
        }

        private void RefreshTitle()
        {
            try
            {
                IntPtr hwnd = GetTargetWindow();
                if (hwnd == IntPtr.Zero)
                {
                    TitleText.Text = LocalizationManager.Instance["WC_NoTarget"];
                    return;
                }
                var sb = new System.Text.StringBuilder(512);
                GetWindowText(hwnd, sb, sb.Capacity);
                string title = sb.ToString().Trim();
                TitleText.Text = string.IsNullOrEmpty(title)
                    ? LocalizationManager.Instance["WC_NoTarget"]
                    : title;
            }
            catch { TitleText.Text = LocalizationManager.Instance["WC_NoTarget"]; }
        }

        private void Action_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag) return;
            IntPtr hwnd = GetTargetWindow();
            if (hwnd == IntPtr.Zero) return;

            switch (tag)
            {
                case "min":
                    WindowAction.ToggleMinimize(hwnd);
                    break;
                case "max":
                    WindowAction.Restore(hwnd);
                    WindowAction.ToggleMaximize(hwnd);
                    break;
                case "close":
                    WindowAction.Close(hwnd);
                    break;
                case "pin":
                    WindowAction.ToggleTopmost(hwnd);
                    break;
                case "snap_left":
                    Snap(hwnd, 0.0, 0.0, 0.5, 1.0);
                    break;
                case "snap_right":
                    Snap(hwnd, 0.5, 0.0, 0.5, 1.0);
                    break;
                case "snap_center":
                    Snap(hwnd, 0.25, 0.0, 0.5, 1.0);
                    break;
            }
        }

        /// <summary>贴靠到目标窗口所在屏幕的区域（比例坐标，DIP）。</summary>
        private static void Snap(IntPtr hwnd, double xRatio, double yRatio, double wRatio, double hRatio)
        {
            try
            {
                // 用前台窗口实际矩形中心确定其所在屏幕（多显示器下贴靠到正确屏幕）
                GetWindowRect(hwnd, out RECT wr);
                double scale = ScreenMetrics.DipScale;
                var wa = ScreenMetrics.GetCachedScreenForWindow(
                    wr.Left / scale + 8, wr.Top / scale + 8, 16, 16); // 窗口左上角附近点
                int x = (int)Math.Round((wa.Left + wa.Width * xRatio) * scale);
                int y = (int)Math.Round((wa.Top + wa.Height * yRatio) * scale);
                int w = (int)Math.Round(wa.Width * wRatio * scale);
                int h = (int)Math.Round(wa.Height * hRatio * scale);
                WindowAction.MoveResize(hwnd, x, y, w, h);
            }
            catch (Exception ex)
            {
                // 用户点了贴靠布局却没生效
                LogManager.Warning($"[窗口操作] 应用贴靠布局失败：{ex.Message}");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}