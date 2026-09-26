using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ShoreHue.UI.Media
{
    /// <summary>
    /// 窗口嵌入宿主（“第二块显示屏”）：
    /// 把目标窗口 SetParent 到面板内的宿主窗口，实现真实内容、真实交互、比例天然正确。
    /// 退出时把窗口恢复为独立顶层窗口。
    /// </summary>
    public class WindowEmbedHost : HwndHost
    {
        private IntPtr _hostHwnd = IntPtr.Zero;
        private IntPtr _targetHwnd = IntPtr.Zero;
        private IntPtr _originalParent = IntPtr.Zero;
        private RECT _originalRect;
        private int _originalStyle;
        private int _originalExStyle;
        private bool _embedded;

        public bool IsEmbedded => _embedded;

        public WindowEmbedHost()
        {
            // 宿主尺寸变化时同步嵌入窗口大小
            SizeChanged += (_, _) => ResizeTarget();
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            _hostHwnd = CreateWindowEx(
                0,
                "static",
                "",
                WS_CHILD | WS_VISIBLE,
                0, 0, 10, 10,
                hwndParent.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            return new HandleRef(this, _hostHwnd);
        }

        /// <summary>
        /// 嵌入目标窗口：记录原始父窗口与位置，SetParent 到宿主。
        /// </summary>
        public bool Attach(IntPtr targetHwnd)
        {
            ShoreHue.Core.Infrastructure.Logging.LogManager.Debug(
                $"[Embed] Attach: target={targetHwnd} host={_hostHwnd}");
            if (targetHwnd == IntPtr.Zero || _hostHwnd == IntPtr.Zero) return false;

            try
            {
                Detach();

                _targetHwnd = targetHwnd;
                // ★ 最小化窗口先恢复再嵌入（否则嵌入后无内容）
                if (IsIconic(targetHwnd))
                {
                    ShowWindow(targetHwnd, SW_RESTORE);
                }
                // ★ 全屏/最大化窗口先还原再嵌入（否则嵌入后仍以全屏尺寸显示）
                if (IsZoomed(targetHwnd))
                {
                    ShowWindow(targetHwnd, SW_RESTORE);
                }
                _originalParent = GetParent(targetHwnd);
                GetWindowRect(targetHwnd, out _originalRect);
                _originalStyle = GetWindowLong(targetHwnd, GWL_STYLE);
                _originalExStyle = GetWindowLong(targetHwnd, GWL_EXSTYLE);

                IntPtr result = SetParent(targetHwnd, _hostHwnd);
                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug(
                    $"[Embed] SetParent ret={result} host={_hostHwnd}");
                if (result == IntPtr.Zero)
                {
                    _targetHwnd = IntPtr.Zero;
                    return false;
                }

                // ★ 去掉独立顶层特征与边框，让窗口内容铺满宿主（“第二块显示屏”效果）
                int style = _originalStyle;
                SetWindowLong(targetHwnd, GWL_STYLE,
                    (style & ~WS_POPUP & ~WS_CAPTION & ~WS_THICKFRAME) | WS_CHILD);
                int exStyle = _originalExStyle;
                SetWindowLong(targetHwnd, GWL_EXSTYLE, exStyle & ~(int)WS_EX_APPWINDOW);
                // ★ 样式变更后必须 FRAMECHANGED，否则边框/标题栏不会真正去掉
                SetWindowPos(targetHwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

                _embedded = true;
                ResizeTarget();
                // ★ 布局可能尚未完成，延迟重试确保嵌入窗口铺满宿主
                Dispatcher.BeginInvoke(new Action(() => ResizeTarget()), System.Windows.Threading.DispatcherPriority.Loaded);
                Dispatcher.BeginInvoke(new Action(() => ResizeTarget()), System.Windows.Threading.DispatcherPriority.Background);
                ShowWindow(targetHwnd, SW_SHOW);
                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug("[Embed] 嵌入成功");
                return true;
            }
            catch (Exception ex)
            {
                ShoreHue.Core.Infrastructure.Logging.LogManager.Debug("[Embed] 嵌入失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 解除嵌入：恢复原始父窗口与位置。
        /// </summary>
        public void Detach(bool showWindow = true)
        {
            if (_embedded && _targetHwnd != IntPtr.Zero)
            {
                try
                {
                    SetWindowLong(_targetHwnd, GWL_STYLE, _originalStyle);
                    SetWindowLong(_targetHwnd, GWL_EXSTYLE, _originalExStyle);
                    SetParent(_targetHwnd, _originalParent == IntPtr.Zero ? IntPtr.Zero : _originalParent);
                    SetWindowPos(_targetHwnd, IntPtr.Zero,
                        _originalRect.Left, _originalRect.Top,
                        _originalRect.Right - _originalRect.Left,
                        _originalRect.Bottom - _originalRect.Top,
                        SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                    // ★ 最小化解除时保持最小化并以最小化状态回到任务栏；停止/切换时恢复显示
                    if (showWindow)
                    {
                        ShowWindow(_targetHwnd, SW_SHOW);
                    }
                    else
                    {
                        ShowWindow(_targetHwnd, SW_SHOWMINNOACTIVE);
                    }
                }
                catch { /* 尽力而为：还原目标窗口失败也不抛（否则会打断 Detach 后面的清理与状态复位） */ }

                _embedded = false;
                _targetHwnd = IntPtr.Zero;
            }
        }

        private void ResizeTarget()
        {
            if (!_embedded || _targetHwnd == IntPtr.Zero || _hostHwnd == IntPtr.Zero) return;
            try
            {
                // ★ 用 WPF 布局尺寸 × DPI 计算物理尺寸，比 GetClientRect 更可靠
                double dpi = 1.0;
                try
                {
                    var source = PresentationSource.FromVisual(this);
                    if (source?.CompositionTarget != null)
                    {
                        dpi = source.CompositionTarget.TransformToDevice.M11;
                    }
                }
                catch { /* 取不到 DPI 就按 100%（下一行的 1.0 兜底同时挡住 NaN/Infinity） */ }
                if (dpi <= 0 || double.IsNaN(dpi) || double.IsInfinity(dpi)) dpi = 1.0;

                int w = Math.Max(1, (int)(ActualWidth * dpi));
                int h = Math.Max(1, (int)(ActualHeight * dpi));

                // ★ 兜底：WPF 布局尚未完成时，用宿主客户区物理尺寸
                if (w < 2 || h < 2)
                {
                    if (GetClientRect(_hostHwnd, out RECT host))
                    {
                        w = Math.Max(1, host.Right - host.Left);
                        h = Math.Max(1, host.Bottom - host.Top);
                    }
                }

                SetWindowPos(_targetHwnd, IntPtr.Zero, 0, 0, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch { /* 尽力而为：被嵌入的窗口没跟着变尺寸（下次布局变化会再试） */ }
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            Detach();
            if (hwnd.Handle != IntPtr.Zero)
            {
                DestroyWindow(hwnd.Handle);
            }
            _hostHwnd = IntPtr.Zero;
        }

        // ================= Win32 =================

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CHILD = 0x40000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const long WS_EX_APPWINDOW = 0x00040000;
        private const int SW_SHOW = 5;
        private const int SW_SHOWMINNOACTIVE = 7;
        private const int SW_RESTORE = 9;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
            int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);
    }
}
