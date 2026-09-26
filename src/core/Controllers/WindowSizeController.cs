using ShoreHue.Animation;
using ShoreHue.Core.Calculators;
using ShoreHue.Core.Detection;
using ShoreHue.Core.Services.Configuration;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShoreHue.Infrastructure.Utils;

namespace ShoreHue.Core.Controllers
{
    public class WindowSizeController
    {
        private readonly Window _window;
        private readonly ContentControl _contentContainer;
        private readonly FrameworkElement _mainPanel;
        private readonly double _taskbarHeight;
        private double _bottomBoundary;
        private readonly ISettingsService _settings;
        private readonly EdgeTriggerController _edgeController;

        private string _currentEdge = "";
        private string _currentMode = "Taskbar";
        private string _currentRegionKey = "Taskbar";
        private bool _isUserResizing = false;

        private readonly SizeCalculator _sizeCalculator;
        private readonly SizePositionCalculator _positionCalculator;
        private readonly SizeDragHandler _dragHandler;

        /// <summary>面板所在显示器工作区尺寸（DIP，替代主屏常量）。</summary>
        private (double width, double height) PanelScreenSize
        {
            get
            {
                var wa = ScreenMetrics.GetCachedScreenForWindow(
                    _window.Left, _window.Top, _window.Width, _window.Height);
                return (wa.Width, wa.Height);
            }
        }

        public event Action<bool>? UserResizeStarted;
        public event Action? SizeChanged;
        public event Action? ResizeEnded;
        public event Action<bool>? LockRequest;

        public string CurrentMode => _currentMode;

        /// <summary>
        /// 注入边缘触发带的启用过滤（与主窗口 tick 的 IsRegionEnabledBySettings 一致）。
        /// 使面板贴屏幕边侧的拖拽手柄在有效边缘触发带内让位。
        /// </summary>
        public void SetEdgeBandRegionEnabled(Func<EdgeRegion, bool>? check)
            => _dragHandler.RegionEnabledCheck = check;

        /// <summary>
        /// 更新底部贴边边界（任务栏自动隐藏/升起时由主窗口定时刷新）。
        /// </summary>
        public void UpdateBottomBoundary(double value)
        {
            if (value > 0 && !double.IsNaN(value) && !double.IsInfinity(value))
            {
                _bottomBoundary = value;
            }
        }

        public WindowSizeController(
            Window window,
            ContentControl contentContainer,
            FrameworkElement mainPanel,
            double taskbarHeight,
            ISettingsService settings,
            EdgeTriggerController edgeController,
            double bottomBoundary)
        {
            _window = window;
            _contentContainer = contentContainer;
            _mainPanel = mainPanel;
            _taskbarHeight = taskbarHeight;
            _bottomBoundary = bottomBoundary;
            _settings = settings;
            _edgeController = edgeController;

            _sizeCalculator = new SizeCalculator(_window, _contentContainer);
            _positionCalculator = new SizePositionCalculator(_window);
            _dragHandler = new SizeDragHandler(_window, _mainPanel, this, _edgeController);

            _dragHandler.UserResizeStarted += (started) =>
            {
                _isUserResizing = started;
                UserResizeStarted?.Invoke(started);
            };
            _dragHandler.ResizeEnded += () =>
            {
                _isUserResizing = false;
                ResizeEnded?.Invoke();
                SaveCurrentSize();
            };
            _dragHandler.LockRequest += (locked) => LockRequest?.Invoke(locked);
        }

        public void SetMode(string mode, string regionKey = "")
        {
            _currentMode = mode;
            if (!string.IsNullOrEmpty(regionKey))
            {
                _currentRegionKey = regionKey;
            }
            _mainPanel.Cursor = Cursors.Arrow;
        }

        public void UpdateRegion(string edge, string regionType)
        {
            _currentEdge = edge;
            _currentRegionKey = string.IsNullOrEmpty(edge) ? regionType : edge + "_" + regionType;
        }

        public void ApplySizeForCurrentMode()
        {
            if (_isUserResizing) return;

            var (userW, userH) = _settings.GetUserSize(_currentRegionKey);

            if (_currentMode == "Taskbar")
            {
                // ★ 允许任务栏面板也保存用户调整后的尺寸
                if (!_settings.AutoFitOnTrigger && userW > 0 && userH > 0)
                {
                    ApplyUserSize(userW, userH);
                }
                else
                {
                    ApplyTaskbarSize();
                }
                return;
            }

            if (_currentMode == "Placeholder")
            {
                ApplyPlaceholderSize();
                return;
            }

            if (_settings.AutoFitOnTrigger)
            {
                ApplyAutoSize();
                return;
            }

            if (userW > 0 && userH > 0)
            {
                ApplyUserSize(userW, userH);
            }
            else
            {
                ApplyAutoSize();
            }
        }

        public void ApplySizeStrategyForWidget()
        {
            ApplySizeForCurrentMode();
        }

        /// <summary>按小组件内容实际尺寸计算目标面板尺寸（内容尽量显示全，已内部限高）。</summary>
        public (double width, double height) MeasureWidgetTargetSize()
        {
            var (cw, ch) = _sizeCalculator.MeasureContent();
            return _sizeCalculator.CalculateTargetSize(cw, ch, "Widget",
                _settings.GoldenRatio, _settings.HorizontalLayoutThreshold);
        }

        /// <summary>应用辅助（画中画/媒体控制）按内容实际尺寸计算目标面板尺寸（限幅 80% 屏）。</summary>
        public (double width, double height) MeasureAppHelperTargetSize()
        {
            var (cw, ch) = _sizeCalculator.MeasureContent();
            return _sizeCalculator.CalculateTargetSize(cw, ch, "AppHelper");
        }

        /// <summary>角落面板（快捷开关/通知/最近）按内容自适应尺寸，避免固定方形过高。</summary>
        public (double width, double height) MeasurePlaceholderTargetSize()
        {
            var (cw, ch) = _sizeCalculator.MeasureContent();
            var (w, h) = _sizeCalculator.CalculateTargetSize(cw, ch, "Placeholder");
            // 角落面板：宽度不超过屏幕 40%，高度不超过 60%，避免过高
            var wa = ScreenMetrics.GetCachedScreenForWindow(
                _window.Left, _window.Top, _window.Width, _window.Height);
            w = Math.Min(w, wa.Width * 0.4);
            h = Math.Min(h, wa.Height * 0.6);
            return (w, h);
        }

        private void ApplyTaskbarSize()
        {
            var (screenWidth, _) = PanelScreenSize;
            double targetWidth = screenWidth * 2.0 / 3.0;
            double targetHeight = Math.Max(_window.MinHeight, Math.Max(60, _taskbarHeight * 1.75));

            if (targetWidth < 200) targetWidth = 200;
            if (targetHeight < 40) targetHeight = 40;

            var (newLeft, newTop) = Anchor(_currentEdge, _window.Left, _window.Top, targetWidth, targetHeight);
            WindowRect.ApplyAtomic(_window, newLeft, newTop, targetWidth, targetHeight);
            SizeChanged?.Invoke();
        }

        private void ApplyPlaceholderSize()
        {
            var (screenWidth, _) = PanelScreenSize;
            double targetSize = screenWidth * 2.0 / 7.0;
            targetSize = Math.Max(100, Math.Min(targetSize, screenWidth * 0.4));

            var (newLeft, newTop) = Anchor(_currentEdge, _window.Left, _window.Top, targetSize, targetSize);
            WindowRect.ApplyAtomic(_window, newLeft, newTop, targetSize, targetSize);
            SizeChanged?.Invoke();
        }

        private void ApplyAutoSize()
        {
            var (contentWidth, contentHeight) = _sizeCalculator.MeasureContent();
            if (contentWidth < 10 || contentHeight < 10)
            {
                contentWidth = 280;
                contentHeight = 160;
            }

            var (targetWidth, targetHeight) = _sizeCalculator.CalculateTargetSize(
                contentWidth, contentHeight, _currentMode,
                _settings.GoldenRatio, _settings.HorizontalLayoutThreshold);

            if (targetWidth < 100) targetWidth = 100;
            if (targetHeight < 60) targetHeight = 60;

            // ★ 把长宽比一起记下来：验证"接近黄金比"时不该靠截图目测，要看这两个数
            //   （以及 ratio 是否≈φ）。真机验证脚本也读这一行。
            ShoreHue.Core.Infrastructure.Logging.LogManager.Debug(
                $"AutoSize content={contentWidth:F0}x{contentHeight:F0} → target={targetWidth:F0}x{targetHeight:F0}" +
                $" ratio={(targetHeight > 0 ? targetWidth / targetHeight : 0):F2} mode={_currentMode}");

            var (newLeft, newTop) = _positionCalculator.CalculatePosition(
                targetWidth, targetHeight, _currentEdge, _window.Left, _window.Top, _window.Width, _window.Height);

            // 底部边缘必须贴任务栏顶部边界，而不是屏幕底部
            if (_currentEdge == "Bottom")
            {
                newTop = _bottomBoundary - targetHeight;
            }

            WindowRect.ApplyAtomic(_window, newLeft, newTop, targetWidth, targetHeight);
            SizeChanged?.Invoke();
        }

        private void ApplyUserSize(double width, double height)
        {
            if (width < 100) width = 280;
            if (height < 60) height = 160;

            var (newLeft, newTop) = Anchor(_currentEdge, _window.Left, _window.Top, width, height);
            WindowRect.ApplyAtomic(_window, newLeft, newTop, width, height);
            SizeChanged?.Invoke();
        }

        public void ApplyMinSize()
        {
            if (_isUserResizing) return;
            if (_currentMode != "Widget") return;
            var (minWidth, minHeight) = _sizeCalculator.CalculateMinSize();
            var (newLeft, newTop) = Anchor(_currentEdge, _window.Left, _window.Top, minWidth, minHeight);
            WindowRect.ApplyAtomic(_window, newLeft, newTop, minWidth, minHeight);
            SaveCurrentSize();
            SizeChanged?.Invoke();
        }

        /// <summary>
        /// 按当前边缘重新锚定窗口位置：贴边侧的坐标始终由目标尺寸决定。
        /// </summary>
        private (double left, double top) Anchor(string edge, double left, double top, double width, double height)
        {
            var (screenW, screenH) = PanelScreenSize;

            return edge switch
            {
                "Top" => (Math.Max(0, Math.Min(left, screenW - width)), 0),
                "Bottom" => (Math.Max(0, Math.Min(left, screenW - width)), _bottomBoundary - height),
                "Left" => (0, Math.Max(0, Math.Min(top, screenH - height))),
                "Right" => (screenW - width, Math.Max(0, Math.Min(top, screenH - height))),
                _ => (Math.Max(0, Math.Min(left, screenW - width)), Math.Max(0, Math.Min(top, screenH - height)))
            };
        }

        public void RefreshMinSizeCache()
        {
            _sizeCalculator.RefreshCache();
        }

        public void ResetForNewTrigger()
        {
            _dragHandler.Reset();
            if (_isUserResizing) return;
            if (_currentMode != "Taskbar")
                ApplySizeForCurrentMode();
        }

        public void TriggerAutoSize(string currentEdge)
        {
            if (_isUserResizing) return;
            _currentEdge = currentEdge;
            _window.Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplySizeForCurrentMode();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        public void UpdateHandlePosition(string edge)
        {
            _currentEdge = edge;
            _dragHandler.UpdateHandlePosition(edge);
        }

        public void RestoreAutoSize()
        {
            if (_isUserResizing) return;
            if (_currentMode == "Taskbar")
            {
                ApplyTaskbarSize();
                return;
            }
            ApplySizeForCurrentMode();
        }

        public void SaveCurrentSize()
        {
            if (_settings.AutoFitOnTrigger) return;

            _settings.SetUserSize(_currentRegionKey, _window.Width, _window.Height);
        }

        public void SaveCurrentSizeWithDelay()
        {
            if (_isUserResizing) return;
            _window.Dispatcher.BeginInvoke(new Action(() =>
            {
                SaveCurrentSize();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        public bool HandleMouseDown(object sender, MouseButtonEventArgs e)
        {
            bool handled = _dragHandler.HandleMouseDown(sender, e, _currentMode);
            if (handled)
            {
                _settings.UseAutoSize = false;
            }
            return handled;
        }

        public void HandleMouseMove(object sender, MouseEventArgs e)
        {
            _dragHandler.HandleMouseMove(sender, e);
        }

        public void HandleMouseUp(object sender, MouseButtonEventArgs e)
        {
            bool wasResizing = _isUserResizing;
            _dragHandler.HandleMouseUp(sender, e);
            _isUserResizing = false;

            // ★ 只有真正发生过拖拽缩放时才写入配置；
            //   否则每次点击（含任务栏关闭按钮）都会 Save → SettingsChanged → 布局重排，
            //   导致按钮 Click 在 MouseUp 阶段丢失。
            if (wasResizing)
            {
                _settings.UseAutoSize = false;
                SaveCurrentSizeWithDelay();
            }
        }

        internal void OnSizeChanged() => SizeChanged?.Invoke();
        internal void OnResizeEnded() => ResizeEnded?.Invoke();
    }
}