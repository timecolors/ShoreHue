using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace ShoreHue.UI.Media
{
    /// <summary>
    /// DWM 缩略图管理（镜像窗口的高性能方案）：
    /// 用 DwmRegisterThumbnail 把源窗口内容实时合成到面板主窗口的指定客户区，
    /// 由 GPU 直接合成，帧率等于源窗口渲染帧率，远超 PrintWindow 抓帧。
    /// 注意：DWM 缩略图目标必须是顶层窗口（子窗口会返回 E_INVALIDARG）。
    /// 仅用于截图镜像（不移动原窗口）；点击交互仍由 PanelMediaPlayer 转发。
    /// </summary>
    public class WindowThumbnailHost
    {
        private IntPtr _targetHwnd = IntPtr.Zero;
        private IntPtr _sourceHwnd = IntPtr.Zero;
        private IntPtr _thumbnail = IntPtr.Zero;
        private bool _registered;

        public bool IsActive => _registered;

        /// <summary>在目标顶层窗口上注册源窗口缩略图；失败返回 false。</summary>
        public bool Attach(IntPtr targetWindowHwnd, IntPtr sourceHwnd)
        {
            Detach();
            if (targetWindowHwnd == IntPtr.Zero || sourceHwnd == IntPtr.Zero) return false;

            int hr = DwmRegisterThumbnail(targetWindowHwnd, sourceHwnd, out _thumbnail);
            if (hr != 0 || _thumbnail == IntPtr.Zero)
            {
                _thumbnail = IntPtr.Zero;
                return false;
            }

            _targetHwnd = targetWindowHwnd;
            _sourceHwnd = sourceHwnd;
            _registered = true;
            return true;
        }

        public void Detach()
        {
            if (_registered && _thumbnail != IntPtr.Zero)
            {
                // 尽力而为：注销失败也无害（缩略图随宿主窗口销毁自动清理）
                try { DwmUnregisterThumbnail(_thumbnail); }
                catch { /* P/Invoke 失败无副作用，不再套一层日志 */ }
            }
            _registered = false;
            _thumbnail = IntPtr.Zero;
            _targetHwnd = IntPtr.Zero;
            _sourceHwnd = IntPtr.Zero;
        }

        /// <summary>
        /// 更新缩略图显示区域。destDips 为目标窗口客户区内的矩形（DIP），
        /// dpi 为窗口缩放比例；内部转为物理像素后交给 DWM。
        /// </summary>
        public void UpdateDestination(Rect destDips, double dpi)
        {
            if (!_registered || _thumbnail == IntPtr.Zero) return;
            if (dpi <= 0 || double.IsNaN(dpi) || double.IsInfinity(dpi)) dpi = 1.0;

            int x = (int)Math.Round(destDips.X * dpi);
            int y = (int)Math.Round(destDips.Y * dpi);
            int w = Math.Max(1, (int)Math.Round(destDips.Width * dpi));
            int h = Math.Max(1, (int)Math.Round(destDips.Height * dpi));

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_VISIBLE | DWM_TNP_RECTDESTINATION | DWM_TNP_SOURCECLIENTAREAONLY,
                fVisible = true,
                fSourceClientAreaOnly = true,
                rcDestination = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h }
            };
            DwmUpdateThumbnailProperties(_thumbnail, ref props);
        }

        // ================= DWM =================

        private const uint DWM_TNP_RECTDESTINATION = 0x00000001;
        private const uint DWM_TNP_VISIBLE = 0x00000008;
        private const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_THUMBNAIL_PROPERTIES
        {
            public uint dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            public bool fVisible;
            public bool fSourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);
    }
}
