using System;
using ShoreHue.Core.Infrastructure.Logging;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ShoreHue.Infrastructure.WinApi
{
    /// <summary>
    /// 窗口内容捕获：通过 PrintWindow 抓取任意窗口（浏览器等）的画面，
    /// 用于画中画镜像显示。DWM 合成环境下对绝大多数窗口有效。
    /// </summary>
    public static class WindowCaptureService
    {
        private const uint PW_RENDERFULLCONTENT = 0x00000002;
        private const int MaxCaptureWidth = 1280;

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

        public static bool IsWindowAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

        /// <summary>获取窗口客户区尺寸（物理像素）。失败返回 false。</summary>
        public static bool GetClientSize(IntPtr hwnd, out int width, out int height)
        {
            width = height = 0;
            if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out RECT client)) return false;
            width = client.Right - client.Left;
            height = client.Bottom - client.Top;
            return width > 0 && height > 0;
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_MOUSEWHEEL = 0x020A;
        private const uint MK_LBUTTON = 0x0001;
        private const uint MK_RBUTTON = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        public enum MouseMessage
        {
            LeftDown,
            LeftUp,
            RightDown,
            RightUp,
            Move
        }

        /// <summary>
        /// 捕获窗口当前画面。窗口无效/最小化时返回 null。
        /// </summary>
        public static BitmapSource? Capture(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || IsIconic(hwnd)) return null;

            // ★ 整窗渲染 + 客户区裁剪：
            //   PrintWindow 渲染的是整个窗口（含标题栏/边框），若位图尺寸=客户区，
            //   内容会整体下移、底部被裁。先按整窗渲染，再按客户区在窗口内的偏移裁剪。
            if (!GetWindowRect(hwnd, out RECT win)) return null;
            if (!GetClientRect(hwnd, out RECT client)) return null;

            int winW = win.Right - win.Left;
            int winH = win.Bottom - win.Top;
            int clientW = client.Right - client.Left;
            int clientH = client.Bottom - client.Top;
            if (winW <= 0 || winH <= 0 || clientW <= 0 || clientH <= 0 ||
                winW > 8192 || winH > 8192) return null;

            // 客户区在窗口内的偏移（边框 + 标题栏）：
            // GetClientRect 的 Left/Top 恒为 0，必须用 ClientToScreen 取客户区左上角屏幕坐标
            var clientOrigin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref clientOrigin)) return null;
            int offsetX = clientOrigin.X - win.Left;
            int offsetY = clientOrigin.Y - win.Top;
            if (offsetX < 0) offsetX = 0;
            if (offsetY < 0) offsetY = 0;

            // 限制捕获尺寸以控制性能，保持宽高比
            double scale = Math.Min(1.0, (double)MaxCaptureWidth / winW);
            int cw = Math.Max(1, (int)Math.Round(winW * scale));
            int ch = Math.Max(1, (int)Math.Round(winH * scale));

            using var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }

            // 裁剪客户区（缩放后）
            int cropX = Math.Min(cw, (int)Math.Round(offsetX * scale));
            int cropY = Math.Min(ch, (int)Math.Round(offsetY * scale));
            int cropW = Math.Min(cw - cropX, Math.Max(1, (int)Math.Round(clientW * scale)));
            int cropH = Math.Min(ch - cropY, Math.Max(1, (int)Math.Round(clientH * scale)));

            IntPtr hbitmap = IntPtr.Zero;
            try
            {
                using var crop = bmp.Clone(new Rectangle(cropX, cropY, cropW, cropH), PixelFormat.Format32bppArgb);
                hbitmap = crop.GetHbitmap();
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hbitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch (Exception ex)
            {
                // 返回 null → 调用方显示占位（窗口镜像/缩略图空白，用户可见）
                LogManager.Debug($"[窗口捕获] 截取窗口位图失败（显示占位）：{ex.Message}");
                return null;
            }
            finally
            {
                if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            }
        }

        /// <summary>
        /// 将镜像区域的鼠标位置映射到源窗口客户端坐标。
        /// 镜像图像按 Uniform 拉伸，因此先计算实际渲染矩形，再做归一化映射。
        /// </summary>
        public static bool MapToClient(IntPtr hwnd, System.Windows.Point pos, double areaWidth, double areaHeight,
            double frameWidth, double frameHeight, out int clientX, out int clientY)
        {
            clientX = clientY = 0;
            if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out RECT client)) return false;
            if (areaWidth <= 1 || areaHeight <= 1 || frameWidth <= 1 || frameHeight <= 1) return false;

            double scale = Math.Min(areaWidth / frameWidth, areaHeight / frameHeight);
            double renderW = frameWidth * scale;
            double renderH = frameHeight * scale;
            double offsetX = (areaWidth - renderW) / 2.0;
            double offsetY = (areaHeight - renderH) / 2.0;

            if (pos.X < offsetX || pos.X > offsetX + renderW ||
                pos.Y < offsetY || pos.Y > offsetY + renderH)
            {
                return false;
            }

            double nx = (pos.X - offsetX) / renderW;
            double ny = (pos.Y - offsetY) / renderH;

            int cw = Math.Max(1, client.Right - client.Left);
            int ch = Math.Max(1, client.Bottom - client.Top);
            clientX = (int)Math.Round(nx * cw);
            clientY = (int)Math.Round(ny * ch);
            return true;
        }

        public static void SetForeground(IntPtr hwnd)
        {
            // 尽力而为：前台锁（Windows 限制）会让这次置前无效，不影响其它逻辑
            try { SetForegroundWindow(hwnd); }
            catch (Exception ex) { LogManager.Debug($"[窗口捕获] 置前窗口失败（无害）：{ex.Message}"); }
        }

        public static void SendMouseEvent(IntPtr hwnd, MouseMessage message, int clientX, int clientY)
        {
            if (hwnd == IntPtr.Zero) return;
            uint msg = message switch
            {
                MouseMessage.LeftDown => WM_LBUTTONDOWN,
                MouseMessage.LeftUp => WM_LBUTTONUP,
                MouseMessage.RightDown => WM_RBUTTONDOWN,
                MouseMessage.RightUp => WM_RBUTTONUP,
                _ => WM_MOUSEMOVE
            };
            IntPtr wParam = message switch
            {
                MouseMessage.LeftDown or MouseMessage.Move => (IntPtr)MK_LBUTTON,
                MouseMessage.RightDown => (IntPtr)MK_RBUTTON,
                _ => IntPtr.Zero
            };
            IntPtr lParam = (IntPtr)((clientY << 16) | (clientX & 0xFFFF));
            // 用 PostMessage 异步投递，避免源窗口消息循环繁忙时阻塞面板 UI
            PostMessage(hwnd, msg, wParam, lParam);
        }

        /// <summary>
        /// 转发鼠标滚轮：WM_MOUSEWHEEL 的 lParam 约定为屏幕坐标（不是客户区坐标），
        /// wParam 高 16 位为滚动增量（120 的倍数）。
        /// </summary>
        public static void SendMouseWheel(IntPtr hwnd, int delta, int screenX, int screenY)
        {
            if (hwnd == IntPtr.Zero) return;
            IntPtr wParam = (IntPtr)((delta << 16) & 0xFFFF0000);
            IntPtr lParam = (IntPtr)((screenY << 16) | (screenX & 0xFFFF));
            PostMessage(hwnd, WM_MOUSEWHEEL, wParam, lParam);
        }
    }
}
