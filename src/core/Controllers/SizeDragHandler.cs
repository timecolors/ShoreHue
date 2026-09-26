using ShoreHue.Core.Detection;
using ShoreHue.Core.Infrastructure.Logging;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace ShoreHue.Core.Controllers
{
    /// <summary>
    /// 面板调整大小：用 WM_NCLBUTTONDOWN + 命中区域码（HTTOP/HTBOTTOM 等）
    /// 让 Windows 原生接管 resize——与普通窗口行为完全一致，无中间态、不抽搐。
    /// 拖哪条边哪条边跟随鼠标，对侧固定。
    /// </summary>
    public class SizeDragHandler
    {
        private readonly Window _window;
        private readonly FrameworkElement _mainPanel;
        private readonly WindowSizeController _controller;
        private readonly EdgeTriggerController _edgeController;

        internal enum ResizeHandle
        {
            Top, Bottom, Left, Right,
            TopLeft, TopRight, BottomLeft, BottomRight
        }

        private ResizeHandle? _activeHandle;

        public event Action<bool>? UserResizeStarted;
        public event Action? ResizeEnded;
        public event Action<bool>? LockRequest;

        /// <summary>
        /// 边缘触发带的启用过滤（与主窗口 tick 的 IsRegionEnabledBySettings 一致）。
        /// 为 null 时不额外过滤，仅以"鼠标位于屏幕边缘触发带内"为准。
        /// </summary>
        public Func<EdgeRegion, bool>? RegionEnabledCheck { get; set; }

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        public SizeDragHandler(Window window, FrameworkElement mainPanel, WindowSizeController controller, EdgeTriggerController edgeController)
        {
            _window = window;
            _mainPanel = mainPanel;
            _controller = controller;
            _edgeController = edgeController;

            _mainPanel.MouseLeave += OnMainPanelMouseLeave;
        }

        public void Reset()
        {
            _activeHandle = null;
            if (_edgeController != null) _edgeController.IsDragging = false;
            try
            {
                if (_mainPanel.IsMouseCaptured)
                    _mainPanel.ReleaseMouseCapture();
            }
            catch (Exception ex)
            {
                // 鼠标捕获没释放 → 之后鼠标一直被面板"吸住"（用户可见）
                LogManager.Warning($"[调整尺寸] 重置时释放鼠标捕获失败（鼠标可能被面板吸住）：{ex.Message}");
            }
            Mouse.OverrideCursor = null;
        }

        public void UpdateHandlePosition(string edge)
        {
            // 手柄由鼠标位置实时决定，无需预置
        }

        public bool HandleMouseDown(object sender, MouseButtonEventArgs e, string mode)
        {
            try
            {
                var pos = e.GetPosition(_mainPanel);
                var handle = GetHandleAt(pos);

                if (e.ClickCount == 2 && handle.HasValue)
                {
                    _controller.RestoreAutoSize();
                    e.Handled = true;
                    return true;
                }

                if (!handle.HasValue) return false;

                _activeHandle = handle!.Value;
                UserResizeStarted?.Invoke(true);
                LockRequest?.Invoke(true);
                _edgeController.IsDragging = true;

                // ★ 原生系统 resize：Windows 接管，行为与普通窗口一致
                var hwnd = new WindowInteropHelper(_window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int ht = ToHitTest(handle.Value);
                    SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)ht, IntPtr.Zero);
                }

                // SendMessage 返回 = resize 结束
                FinishResize();
                e.Handled = true;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SizeDragHandler.HandleMouseDown error: {ex.Message}");
                ForceRelease();
            }

            return false;
        }

        private void FinishResize()
        {
            _activeHandle = null;
            _edgeController.IsDragging = false;
            _edgeController.NotifyDragEnded();
            UserResizeStarted?.Invoke(false);
            LockRequest?.Invoke(false);
            ResizeEnded?.Invoke();
            _controller.SaveCurrentSizeWithDelay();
        }

        private static int ToHitTest(ResizeHandle handle) => handle switch
        {
            ResizeHandle.Top => HTTOP,
            ResizeHandle.Bottom => HTBOTTOM,
            ResizeHandle.Left => HTLEFT,
            ResizeHandle.Right => HTRIGHT,
            ResizeHandle.TopLeft => HTTOPLEFT,
            ResizeHandle.TopRight => HTTOPRIGHT,
            ResizeHandle.BottomLeft => HTBOTTOMLEFT,
            ResizeHandle.BottomRight => HTBOTTOMRIGHT,
            _ => HTTOP
        };

        public void HandleMouseMove(object sender, MouseEventArgs e)
        {
            // 原生 resize 期间无需手动计算位置；这里只负责手柄光标反馈
            try
            {
                var pos = e.GetPosition(_mainPanel);
                var handle = GetHandleAt(pos);
                _mainPanel.Cursor = handle.HasValue ? GetHandleCursor(handle.Value) : Cursors.Arrow;
                // ★ 不用 Mouse.OverrideCursor：它是进程级全局，鼠标移到设置窗口等其他窗口时
                //   残留 SizeWE 清不掉 → 别处到处双箭头。手柄光标用局部 _mainPanel.Cursor 即可，
                //   鼠标离开面板自动恢复（竖条 Hand 与手柄互斥由局部光标优先级自然处理）。
            }
            catch (Exception ex)
            {
                // 只是手柄光标反馈（UI 装饰）：失败就保持上一个光标，不影响调整尺寸
                LogManager.Debug($"[调整尺寸] 更新手柄光标失败（仅光标反馈）：{ex.Message}");
            }
        }

        public void HandleMouseUp(object sender, MouseButtonEventArgs e)
        {
            // 原生模式由 SendMessage 返回时统一收尾；此处保留为空实现以兼容调用链
            if (_activeHandle.HasValue)
            {
                _activeHandle = null;
                _edgeController.IsDragging = false;
                _edgeController.NotifyDragEnded();
                UserResizeStarted?.Invoke(false);
                LockRequest?.Invoke(false);
                ResizeEnded?.Invoke();
            }
        }

        private void OnMainPanelMouseLeave(object sender, MouseEventArgs e)
        {
            // 原生 resize 由系统捕获鼠标，不因暂时离开面板而中断
        }

        private void ForceRelease()
        {
            _activeHandle = null;
            if (_edgeController != null) _edgeController.IsDragging = false;
            _edgeController?.NotifyDragEnded();
            try
            {
                if (_mainPanel.IsMouseCaptured)
                    _mainPanel.ReleaseMouseCapture();
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[调整尺寸] 强制释放鼠标捕获失败（鼠标可能被面板吸住）：{ex.Message}");
            }
            try
            {
                LockRequest?.Invoke(false);
                UserResizeStarted?.Invoke(false);
                ResizeEnded?.Invoke();
            }
            catch (Exception ex)
            {
                // 订阅者抛异常 → 收尾动作（解锁/隐藏面板）可能只做了一半，必须留痕
                LogManager.Warning($"[调整尺寸] 通知尺寸调整结束失败（面板可能未解锁）：{ex.Message}");
            }
            Mouse.OverrideCursor = null;
        }

        private ResizeHandle? GetHandleAt(Point pos)
        {
            // ★ 屏幕边缘触发带优先：鼠标落在有效边缘触发带内时，面板贴边侧的手柄整体让位，
            //   避免"想切边缘内容却变成拖拽/调整大小"。面板内侧（离屏幕边缘超过触发距离）仍为正常拖拽区。
            if (IsInEdgeTriggerBand(pos)) return null;

            return HitTest(_mainPanel.ActualWidth, _mainPanel.ActualHeight, pos);
        }

        /// <summary>
        /// 纯函数手柄命中判定（可单测）。
        /// ★ 手柄区随面板尺寸自适应：矮条/窄条面板（任务栏 / 左右边缘小组件）缩小角区与边带，
        ///   避免覆盖图标点击区。原固定 corner=42 / edgeSize=8 在 86px 高任务栏面板上
        ///   角区占 49% 高度，会盖住右侧图标（点击变成调整大小）。
        /// </summary>
        internal static ResizeHandle? HitTest(double width, double height, Point pos)
        {
            if (width < 10 || height < 10) return null;

            double minSide = Math.Min(width, height);
            double corner = Math.Min(24, minSide * 0.22);   // 常规面板 24px（原 42 过大）；矮条面板更小
            double edgeSize = Math.Min(6, minSide * 0.10);  // 边带最多 6px（原 8）

            bool cLeft = pos.X < corner;
            bool cRight = pos.X > width - corner;
            bool cTop = pos.Y < corner;
            bool cBottom = pos.Y > height - corner;

            if (cLeft && cTop) return ResizeHandle.TopLeft;
            if (cRight && cTop) return ResizeHandle.TopRight;
            if (cLeft && cBottom) return ResizeHandle.BottomLeft;
            if (cRight && cBottom) return ResizeHandle.BottomRight;

            bool left = pos.X < edgeSize;
            bool right = pos.X > width - edgeSize;
            bool top = pos.Y < edgeSize;
            bool bottom = pos.Y > height - edgeSize;
            if (top) return ResizeHandle.Top;
            if (bottom) return ResizeHandle.Bottom;
            if (left) return ResizeHandle.Left;
            if (right) return ResizeHandle.Right;
            return null;
        }

        /// <summary>
        /// 判定鼠标当前位置是否落在"有效屏幕边缘触发带"内（复用 EdgeStateDetector 的共享判定）。
        /// </summary>
        private bool IsInEdgeTriggerBand(Point pos)
            => EdgeBandHelper.IsInEdgeTriggerBand(_window, pos, _edgeController.TriggerDistancePx, RegionEnabledCheck);

        private Cursor GetHandleCursor(ResizeHandle position)
        {
            return position switch
            {
                ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS,
                ResizeHandle.Left or ResizeHandle.Right => Cursors.SizeWE,
                ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
                ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
                _ => Cursors.Arrow
            };
        }
    }
}
