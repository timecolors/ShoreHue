using ShoreHue.Core.Detection;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ShoreHue.Core.Infrastructure.Logging;
using ShoreHue.Core.Services.Configuration;

namespace ShoreHue.Core.Controllers
{
    /// <summary>
    /// 面板拖动（仅“固定位置”模式可用）：
    /// 用 WM_NCLBUTTONDOWN + HTCAPTION 让 Windows 原生接管窗口拖动，
    /// 系统级移动无中间态、无抖动；拖完保存偏移量，下次呼出按偏移定位。
    /// </summary>
    public class DragController
    {
        private readonly Window _window;
        private readonly FrameworkElement _dragTarget;
        private readonly EdgeTriggerController _edgeController;
        private readonly PanelVisibilityController _visibilityController;
        private readonly ISettingsService _settings;

        private bool _isDragging = false;
        private DateTime _lastDragEndTime = DateTime.MinValue;

        public bool IsDragging => _isDragging;
        public bool IsRecentlyDragged => (DateTime.Now - _lastDragEndTime).TotalMilliseconds < 500;

        /// <summary>
        /// 边缘触发带的启用过滤（与主窗口 tick 的 IsRegionEnabledBySettings 一致）。
        /// 为 null 时不额外过滤，仅以"鼠标位于屏幕边缘触发带内"为准。
        /// </summary>
        public Func<EdgeRegion, bool>? RegionEnabledCheck { get; set; }

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        public DragController(
            Window window,
            FrameworkElement dragTarget,
            EdgeTriggerController edgeController,
            PanelVisibilityController visibilityController,
            ISettingsService settings)
        {
            _window = window;
            _dragTarget = dragTarget;
            _edgeController = edgeController;
            _visibilityController = visibilityController;
            _settings = settings;

            _dragTarget.MouseLeftButtonDown += OnMouseLeftButtonDown;
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // ★ 屏幕边缘触发带优先：鼠标在有效边缘触发带内按下时不启动面板拖动，
                //   避免"想切边缘内容却变成拖动面板"。
                var pos = e.GetPosition(_window);
                if (EdgeBandHelper.IsInEdgeTriggerBand(_window, pos, _edgeController.TriggerDistancePx, RegionEnabledCheck))
                    return;

                string currentEdge = _edgeController.CurrentEdge;
                if (string.IsNullOrEmpty(currentEdge) || _settings.GetEdgeMode(currentEdge) != "Fixed")
                    return;

                _visibilityController.SetPanelLock(true);
                _visibilityController.CancelHide();
                _edgeController.IsDragging = true;
                _isDragging = true;

                var hwnd = new WindowInteropHelper(_window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    // ★ 原生窗口拖动：系统接管移动，SendMessage 阻塞直到释放鼠标
                    SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                }

                FinishDrag();
                e.Handled = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DragController.OnMouseLeftButtonDown error: {ex.Message}");
                ForceRelease();
            }
        }

        private void FinishDrag()
        {
            _isDragging = false;
            _edgeController.IsDragging = false;
            _edgeController.NotifyDragEnded();
            _lastDragEndTime = DateTime.Now;

            var wa = ShoreHue.Infrastructure.Utils.ScreenMetrics.GetCachedScreenForWindow(
                _window.Left, _window.Top, _window.Width, _window.Height);
            double screenWidth = wa.Width;
            double screenHeight = wa.Height;
            double offset = 0;
            string edge = _edgeController.CurrentEdge;

            switch (edge)
            {
                case "Top":
                case "Bottom":
                    offset = _window.Left - (screenWidth / 2 - _window.Width / 2);
                    break;
                case "Left":
                case "Right":
                    offset = _window.Top - (screenHeight / 2 - _window.Height / 2);
                    break;
            }
            _settings.SetFixedOffset(edge, offset);

            _window.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _visibilityController.SetPanelLock(false);
                    _visibilityController.ForceHide();
                }
                catch (Exception ex)
                {
                    // 拖拽收尾没隐藏/没解锁 → 面板可能停在"锁住"状态不再自动隐藏（用户可见）
                    LogManager.Warning($"[拖拽] 拖拽收尾时隐藏面板失败（面板可能停在锁定态）：{ex.Message}");
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ForceRelease()
        {
            _isDragging = false;
            _edgeController.IsDragging = false;
            _edgeController.NotifyDragEnded();
            try { _visibilityController.SetPanelLock(false); }
            catch (Exception ex) { LogManager.Warning($"[拖拽] 释放面板锁定失败（面板可能不再自动隐藏）：{ex.Message}"); }
            Mouse.OverrideCursor = null;
        }

        public void Detach()
        {
            ForceRelease();
            _dragTarget.MouseLeftButtonDown -= OnMouseLeftButtonDown;
        }
    }
}