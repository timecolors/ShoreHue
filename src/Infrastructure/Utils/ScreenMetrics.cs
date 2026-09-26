using System;
using System.Windows;

namespace ShoreHue.Infrastructure.Utils
{
    /// <summary>
    /// 多显示器屏幕边界提供器：
    ///  - GetScreenForPoint：鼠标所在显示器的整屏边界（DIP）
    ///  - GetScreenForWindow：窗口中心所在显示器的整屏边界（DIP）
    /// 替代 SystemParameters.PrimaryScreen*（只认主屏）在副屏上的错误表现。
    ///
    /// ★ 语义说明：返回整屏 Bounds（物理屏幕矩形）而不是 WorkingArea（工作区）。
    ///   任务栏顶部边界由 _bottomBoundary 单独管理；用工作区会让底部面板悬空、atBottom 判定错乱。
    ///
    /// ★ DPI 关键：DipScale 必须与 WPF 的 SystemParameters.PrimaryScreen* 基准一致。
    ///   Graphics.FromHwnd 在 PerMonitorV2 进程下可能返回 96（=1.0），导致物理像素没有换算成 DIP，
    ///   所有尺寸/位置被放大。这里用 主屏物理像素宽 ÷ WPF 主屏 DIP 宽 反推真实缩放，稳定可靠。
    /// </summary>
    public static class ScreenMetrics
    {
        private static double _dipScale = 0; // 0 = 未初始化

        // 两个查询各自独立缓存，避免互相污染
        private const double MaxCacheAgeMs = 200;
        private static System.Drawing.Rectangle _windowCache = System.Drawing.Rectangle.Empty;
        private static DateTime _windowCacheTime = DateTime.MinValue;
        private static System.Drawing.Rectangle _pointCache = System.Drawing.Rectangle.Empty;
        private static DateTime _pointCacheTime = DateTime.MinValue;

        /// <summary>实际缩放比例（物理像素 ÷ DIP）。与 SystemParameters.PrimaryScreen* 基准一致。</summary>
        public static double DipScale
        {
            get
            {
                if (_dipScale > 0) return _dipScale;
                try
                {
                    double physW = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920;
                    double dipW = SystemParameters.PrimaryScreenWidth;
                    if (physW > 0 && dipW > 0)
                    {
                        _dipScale = physW / dipW;
                    }
                }
                catch { /* 取不到 DPI 缩放就按 100% 算（下面 _dipScale<=0 有兜底） */ }
                if (_dipScale <= 0) _dipScale = 1.0;
                return _dipScale;
            }
        }

        /// <summary>鼠标所在显示器的整屏边界（DIP，含任务栏区域）。</summary>
        public static Rect GetScreenForPoint(double mouseXDip, double mouseYDip)
        {
            double scale = DipScale;
            var pt = new System.Drawing.Point((int)Math.Round(mouseXDip * scale), (int)Math.Round(mouseYDip * scale));
            return ToDip(System.Windows.Forms.Screen.FromPoint(pt).Bounds, scale);
        }

        /// <summary>窗口中心所在显示器的整屏边界（DIP）。窗口跨屏时按中心归属。</summary>
        public static Rect GetScreenForWindow(double leftDip, double topDip, double widthDip, double heightDip)
        {
            double scale = DipScale;
            int cx = (int)Math.Round((leftDip + widthDip / 2) * scale);
            int cy = (int)Math.Round((topDip + heightDip / 2) * scale);
            return ToDip(System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cx, cy)).Bounds, scale);
        }

        /// <summary>带 200ms 缓存的窗口屏查询（动画每帧调用时避免高频 Win32）。独立缓存。</summary>
        public static Rect GetCachedScreenForWindow(double leftDip, double topDip, double widthDip, double heightDip)
        {
            double scale = DipScale;
            int cx = (int)Math.Round((leftDip + widthDip / 2) * scale);
            int cy = (int)Math.Round((topDip + heightDip / 2) * scale);
            var pt = new System.Drawing.Point(cx, cy);
            if ((DateTime.Now - _windowCacheTime).TotalMilliseconds > MaxCacheAgeMs ||
                !_windowCache.Contains(pt))
            {
                _windowCache = System.Windows.Forms.Screen.FromPoint(pt).Bounds;
                _windowCacheTime = DateTime.Now;
            }
            return ToDip(_windowCache, scale);
        }

        /// <summary>带 200ms 缓存的鼠标屏查询。独立缓存。</summary>
        public static Rect GetCachedScreenForPoint(double mouseXDip, double mouseYDip)
        {
            double scale = DipScale;
            var pt = new System.Drawing.Point((int)Math.Round(mouseXDip * scale), (int)Math.Round(mouseYDip * scale));
            if ((DateTime.Now - _pointCacheTime).TotalMilliseconds > MaxCacheAgeMs ||
                !_pointCache.Contains(pt))
            {
                _pointCache = System.Windows.Forms.Screen.FromPoint(pt).Bounds;
                _pointCacheTime = DateTime.Now;
            }
            return ToDip(_pointCache, scale);
        }

        private static Rect ToDip(System.Drawing.Rectangle phys, double scale)
        {
            if (scale <= 0) scale = 1.0;
            return new Rect(phys.Left / scale, phys.Top / scale, phys.Width / scale, phys.Height / scale);
        }
    }
}