using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ShoreHue.Core;
using ShoreHue.Core.Detection;
using ShoreHue.Core.Services;
using ShoreHue.UI.Status;

namespace ShoreHue.UI.Main
{
    public partial class MainWindow
    {
        private Point _lastMousePosition = new Point(-1, -1);
        private bool _hasLastMousePosition = false;

        // ========== 面板鼠标事件 ==========

        private void MainPanel_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_modeService.IsDoNotDisturb) return;

            _visibilityController.CancelHide();
            // ★ 仅面板未显示（或滑出途中）时才重新 ShowAt：正常显示中鼠标在面板内滑动
            //   会反复触发 MouseEnter（面板跟手滞后），此时 ShowAt 的 RepositionOffscreenForSide
            //   会把面板瞬移到屏幕外再滑入 → 快速滑动时面板横跳闪烁。
            if (!_visibilityController.IsShown || !_visibilityController.IsVisible)
            {
                // 通过锚点滑入/恢复显示（含滑出途中重新进入的情况）
                _edgeController.ShowPanelAtAnchor();
            }
            _lastMousePosition = e.GetPosition(this);
            _hasLastMousePosition = true;
        }

        private void MainPanel_MouseLeave(object sender, MouseEventArgs e)
        {
            // ★ 尺寸调整中（_edgeController.IsDragging）不因鼠标暂时离开而隐藏
            if (_dragController.IsDragging || _dragController.IsRecentlyDragged ||
                _edgeController.IsDragging || _edgeController.IsRecentlyDragged) return;
            if (_visibilityController.IsLocked || PresentationSource.FromVisual(this) == null) return;

            var currentMousePos = e.GetPosition(this);

            if (_hasLastMousePosition)
            {
                double dx = currentMousePos.X - _lastMousePosition.X;
                double dy = currentMousePos.Y - _lastMousePosition.Y;
                if (Math.Sqrt(dx * dx + dy * dy) < 1.0) return;
            }

            _lastMousePosition = currentMousePos;
            _hasLastMousePosition = true;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_dragController.IsDragging || _dragController.IsRecentlyDragged) return;
                if (_visibilityController.IsLocked) return;
                if (_visibilityController.IsMouseNearPanel()) return;
                // ★ 鼠标仍在有效边缘触发带内（面板跟手滞后导致短暂出面板矩形）：
                //   不启动隐藏延时——隐藏只允许在鼠标真正离开面板/所有边缘后计时，
                //   否则快速沿边滑动时面板会被反复 HideWithDelay 并到期隐藏（闪烁）。
                if (IsCursorInActiveEdgeZone()) return;
                _visibilityController.HideWithDelay();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>鼠标当前是否位于"有效边缘触发带"内（已含设置过滤）。
        /// 用于 MouseLeave 延迟回调：面板跟手滞后时鼠标可能短暂位于面板矩形之外，
        /// 但只要仍在边缘触发带内，就不应启动隐藏延时（隐藏只允许在离开面板/所有边缘后计时）。</summary>
        private bool IsCursorInActiveEdgeZone()
        {
            try
            {
                var point = System.Windows.Forms.Cursor.Position;
                double dpiScale = 1.0;
                var ps = PresentationSource.FromVisual(this);
                if (ps?.CompositionTarget != null)
                {
                    dpiScale = ps.CompositionTarget.TransformToDevice.M11;
                }
                if (dpiScale <= 0 || double.IsNaN(dpiScale) || double.IsInfinity(dpiScale)) dpiScale = 1.0;

                double mx = point.X / dpiScale;
                double my = point.Y / dpiScale;
                var wa = ShoreHue.Infrastructure.Utils.ScreenMetrics.GetCachedScreenForPoint(mx, my);
                bool allowTopRight = _settingsService.GetRegionPanel("TopRight") == "WindowControl";
                var region = EdgeStateDetector.DetectRegion(
                    mx, my, wa.Width, wa.Height, _settingsService.TriggerDistancePx, allowTopRight);
                if (region == EdgeRegion.Unknown) return false;
                return IsRegionEnabledBySettings(region);
            }
            catch { /* 算不出来就当"不在启用区域" → 不呼出（宁可不弹，也不要误弹） */ return false; }
        }

        // ========== 外观应用 ==========

        private SolidColorBrush? _cachedBgBrush;
        private SolidColorBrush? _cachedTextBrush;

        private void ApplyAppearance()
        {
            // ★ 面板底色：稳定不透明模式（_useDwmCorner 恒 false）→ 画不透明底色
            if (!_useDwmCorner)
            {
                try
                {
                    var bgColor = HexToMediaColor(_settingsService.BackgroundColor);
                    // ★ 缓存 brush：颜色未变不重建（反复点"刷新"触发 ApplyAppearance 时避免重复重绘闪烁）
                    if (_cachedBgBrush == null || _cachedBgBrush.Color != bgColor)
                    {
                        _cachedBgBrush = new SolidColorBrush(bgColor);
                        MainPanel.Background = _cachedBgBrush;
                    }
                }
                catch
                {
                    if (_cachedBgBrush == null || _cachedBgBrush.Color != Color.FromRgb(45, 45, 45))
                    {
                        _cachedBgBrush = new SolidColorBrush(Color.FromRgb(45, 45, 45));
                        MainPanel.Background = _cachedBgBrush;
                    }
                }
            }

            try
            {
                var textColor = HexToMediaColor(_settingsService.TextColor);
                if (_cachedTextBrush == null || _cachedTextBrush.Color != textColor)
                {
                    _cachedTextBrush = new SolidColorBrush(textColor);
                    // 图标图形已移除，无需设置 IconPath.Stroke
                }
            }
            catch { /* 只影响配色（面板照常显示，用上一次的颜色） */ }

            double targetOpacity = _settingsService.Opacity;
            if (Math.Abs(_visibilityController.Opacity - targetOpacity) > 0.001)
                _visibilityController.Opacity = targetOpacity;
            double targetRadius = _settingsService.CornerRadius;
            if (Math.Abs(MainPanel.CornerRadius.TopLeft - targetRadius) > 0.001)
                MainPanel.CornerRadius = new CornerRadius(targetRadius);
            UpdateIconTextInternal();
        }

        // ========== 系统状态刷新 ==========

        private void RefreshSystemStatus()
        {
            try
            {
                if (_settingsService.ShowSystemStatus)
                {
                    if (SystemStatusContainer.Content is not ShoreHue.UI.Status.SystemStatusView statusView)
                    {
                        statusView = new ShoreHue.UI.Status.SystemStatusView();
                        SystemStatusContainer.Content = statusView;
                    }
                    statusView.ApplySettings(_settingsService);
                    SystemStatusContainer.Visibility = Visibility.Visible;
                }
                else
                {
                    SystemStatusContainer.Visibility = Visibility.Collapsed;
                    SystemStatusContainer.Content = null;
                }
            }
            catch { /* 尽力而为：状态栏区隐藏不掉最多留一块空白（下次应用设置时会再试） */ }
        }

        // ========== 颜色工具 ==========

        private Color HexToMediaColor(string hex)
        {
            try
            {
                if (string.IsNullOrEmpty(hex)) hex = "#2D2D2D";
                if (hex.StartsWith("#")) hex = hex.Substring(1);
                byte a = 255, r = 0, g = 0, b = 0;
                if (hex.Length == 6)
                {
                    r = Convert.ToByte(hex.Substring(0, 2), 16);
                    g = Convert.ToByte(hex.Substring(2, 2), 16);
                    b = Convert.ToByte(hex.Substring(4, 2), 16);
                }
                else if (hex.Length == 8)
                {
                    a = Convert.ToByte(hex.Substring(0, 2), 16);
                    r = Convert.ToByte(hex.Substring(2, 2), 16);
                    g = Convert.ToByte(hex.Substring(4, 2), 16);
                    b = Convert.ToByte(hex.Substring(6, 2), 16);
                }
                else return Color.FromRgb(45, 45, 45);
                return Color.FromArgb(a, r, g, b);
            }
            catch { /* 颜色串写错（用户手改配置）就用默认深灰，面板照常显示 */ return Color.FromRgb(45, 45, 45); }
        }
    }
}
