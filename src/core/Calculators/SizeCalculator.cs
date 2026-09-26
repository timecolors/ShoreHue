using System;
using System.Windows;
using System.Windows.Controls;
using ShoreHue.Infrastructure.Utils;

namespace ShoreHue.Core.Calculators
{
    /// <summary>
    /// 尺寸计算器：计算目标尺寸、最小尺寸、内容尺寸
    /// </summary>
    public class SizeCalculator
    {
        private readonly Window _window;
        private readonly ContentControl _contentContainer;

        private double _cachedMinWidth = 0;
        private double _cachedMinHeight = 0;
        private bool _hasCachedMinSize = false;

        public SizeCalculator(Window window, ContentControl contentContainer)
        {
            _window = window;
            _contentContainer = contentContainer;
        }

        public void RefreshCache()
        {
            _hasCachedMinSize = false;
            _cachedMinWidth = 0;
            _cachedMinHeight = 0;
        }

        /// <summary>
        /// 测量内容理想尺寸：Measure(Infinity) 取 DesiredSize。
        /// ★ 注意：测量后绝不能再 UpdateLayout——它会用"当前面板约束"重新测量，
        ///   把 DesiredSize 覆盖成当前小尺寸下的值，导致"面板越小测越小"（越切越小）。
        /// </summary>
        public (double width, double height) MeasureContent()
        {
            // ★ 以无限空间测量内容，得到真实理想尺寸。
            //   注意：_contentContainer 是 ScrollViewer，对它 Measure(Infinity) 返回的是视口宽
            //   （而非内容理想宽度），会导致小组件面板被量窄。应直接测量其内部 Content。
            try
            {
                FrameworkElement? target = null;
                if (_contentContainer.Content is FrameworkElement inner)
                {
                    target = inner;
                }
                else
                {
                    var cc = _contentContainer as System.Windows.Controls.ContentControl;
                    target = cc?.Content as FrameworkElement;
                }

                // ★ 特判小组件切换器：测其内部真实内容（绕开 ScrollViewer 视口限制，
                //   否则从 AI 面板划到小组件时面板被量窄）
                if (target is ShoreHue.UI.Widgets.WidgetSwitcher ws)
                {
                    // ★ 测量内部已保证布局就绪；未就绪（首次且无历史）返回保底
                    var m = ws.MeasureContentSize();
                    if (m.HasValue) return m.Value;
                    return (280, 200);
                }

                if (target != null)
                {
                    target.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double cw = target.DesiredSize.Width;
                    double ch = target.DesiredSize.Height;
                    if (cw >= 10 && ch >= 10) return (cw, ch);
                }
                else
                {
                    _contentContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double cw = _contentContainer.DesiredSize.Width;
                    double ch = _contentContainer.DesiredSize.Height;
                    if (cw >= 10 && ch >= 10) return (cw, ch);
                }
            }
            catch
            {
                // 刻意不记日志：这里在测量/动画热路径上（可能每帧都走），下面立刻有 ActualWidth/Height 回退，
                //   量不出来只是当帧尺寸偏一点，记日志会刷屏
            }

            // 回退：ActualWidth / ActualHeight（仅布局已完成的场景）
            double contentWidth = _contentContainer.ActualWidth;
            double contentHeight = _contentContainer.ActualHeight;

            if (contentWidth < 10 || contentHeight < 10)
            {
                if (_contentContainer.Content is FrameworkElement child)
                {
                    child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    if (child.DesiredSize.Width > 10) contentWidth = child.DesiredSize.Width;
                    if (child.DesiredSize.Height > 10) contentHeight = child.DesiredSize.Height;
                }
            }

            // ★★★ 如果仍然无效，返回保底值（而非让后续计算崩溃） ★★★
            if (contentWidth < 10) contentWidth = 280;
            if (contentHeight < 10) contentHeight = 160;

            return (contentWidth, contentHeight);
        }

        /// <summary>
        /// 计算目标尺寸（含最小值和模式限幅）。
        /// ★ goldenRatio / horizontalLayoutThreshold 由调用方从设置传入（本类拿不到设置）：
        ///   φ 用已有的 `GoldenRatio` 设置项（默认 1.618）；
        ///   "竖向还是横向长条"沿用已有的 `HorizontalLayoutThreshold`（宽高比阈值，默认 0.43，
        ///   值越大越倾向竖向），不另立一套判断口径。
        /// </summary>
        public (double width, double height) CalculateTargetSize(
            double contentWidth, double contentHeight, string mode,
            double goldenRatio = 1.618, double horizontalLayoutThreshold = 0.43)
        {
            // ★★★ 确保传入的内容尺寸有效 ★★★
            if (contentWidth < 10) contentWidth = 280;
            if (contentHeight < 10) contentHeight = 160;

            const double paddingX = 40;
            const double paddingY = 30;
            const double minWidth = 340;
            const double minHeight = 260;

            double rawWidth = contentWidth + paddingX;
            double rawHeight = contentHeight + paddingY;

            double targetWidth = Math.Max(minWidth, rawWidth);
            double targetHeight = Math.Max(minHeight, rawHeight);

            var wa = ScreenMetrics.GetCachedScreenForWindow(
                _window.Left, _window.Top, _window.Width, _window.Height);
            double screenW = wa.Width;
            double screenH = wa.Height;

            if (mode == "Widget")
            {
                double maxW = screenW * 2.0 / 5.0;
                double maxH = screenH * 2.0 / 3.0;
                targetWidth = Math.Min(targetWidth, maxW);

                // ★ WidgetSwitcher 固定开销：头部标签栏(≈34) + 底部 Footer(32+8) + 面板 Padding(12)。
                //   仅按内容高度计算会导致底部 footer 被窗口截断/覆盖。
                //   ★ 顺序很重要：先加固定开销、再统一钳制。旧实现是"先钳制 → 加 90 → 再钳制"，
                //   内容本来就接近上限时那 90px 会被第二次钳制吃掉 —— 正好是注释里说要修的那种情况。
                const double widgetFixedOverhead = 90;
                targetHeight = Math.Min(targetHeight + widgetFixedOverhead, maxH);

                // ★ 黄金长宽比（2026-09-13）：**长边 = 短边 × φ**，即"把短边补长"，逼近 φ。
                //   ★ 只加长、绝不压短 —— 压短长边就会重新引入"内容显示不全"（那正是这次要修的）。
                //   "竖向还是横向长条"沿用既有设置 HorizontalLayoutThreshold（宽高比阈值），
                //   所以同一份设置既决定面板内部布局方向、也决定这里往哪个轴补，口径只有一个。
                double phi = goldenRatio > 1.01 ? goldenRatio : 1.618;   // 防呆：设置里被改成 1 或更小
                bool horizontalStrip = contentWidth / Math.Max(1.0, contentHeight) > horizontalLayoutThreshold;
                if (horizontalStrip)
                {
                    // 横向长条：宽是长边 → 高补到 宽 / φ
                    double idealHeight = targetWidth / phi;
                    if (idealHeight > targetHeight) targetHeight = idealHeight;
                }
                else
                {
                    // 竖向长条：高是长边 → 宽补到 高 / φ
                    double idealWidth = targetHeight / phi;
                    if (idealWidth > targetWidth) targetWidth = idealWidth;
                }
            }
            else if (mode == "Placeholder")
            {
                // ★ 角落面板（快捷开关/通知/最近）：内容自适应，限幅防过高
                targetWidth = Math.Min(targetWidth, screenW * 0.4);
                targetHeight = Math.Min(targetHeight, screenH * 0.6);
            }
            else if (mode == "AppHelper")
            {
                targetWidth = Math.Min(targetWidth, screenW * 0.8);
                targetHeight = Math.Min(targetHeight, screenH * 0.8);
            }

            targetWidth = Math.Min(targetWidth, screenW);
            targetHeight = Math.Min(targetHeight, screenH);

            return (targetWidth, targetHeight);
        }

        /// <summary>
        /// 计算最小尺寸（使用缓存）
        /// </summary>
        public (double width, double height) CalculateMinSize()
        {
            if (_hasCachedMinSize && _cachedMinWidth > 0 && _cachedMinHeight > 0)
            {
                return (_cachedMinWidth, _cachedMinHeight);
            }

            _contentContainer.UpdateLayout();

            double contentWidth = _contentContainer.ActualWidth;
            double contentHeight = _contentContainer.ActualHeight;

            if (contentWidth < 10)
            {
                _contentContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                contentWidth = _contentContainer.DesiredSize.Width;
            }
            if (contentHeight < 10)
            {
                _contentContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                contentHeight = _contentContainer.DesiredSize.Height;
            }

            if (contentWidth < 10 || contentHeight < 10)
            {
                _contentContainer.Arrange(new Rect(0, 0, double.PositiveInfinity, double.PositiveInfinity));
                if (contentWidth < 10) contentWidth = _contentContainer.RenderSize.Width;
                if (contentHeight < 10) contentHeight = _contentContainer.RenderSize.Height;
            }

            if (contentWidth < 10 || contentHeight < 10)
            {
                if (_contentContainer.Content is FrameworkElement child)
                {
                    child.UpdateLayout();
                    child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    if (contentWidth < 10) contentWidth = child.DesiredSize.Width;
                    if (contentHeight < 10) contentHeight = child.DesiredSize.Height;
                }
            }

            // ★★★ 保底值 ★★★
            if (contentWidth < 10) contentWidth = 280;
            if (contentHeight < 10) contentHeight = 160;

            const double minWidth = 340;
            const double minHeightForButtons = 300;

            if (contentWidth < minWidth) contentWidth = minWidth;
            if (contentHeight < minHeightForButtons) contentHeight = minHeightForButtons;

            const double paddingX = 30;
            const double paddingY = 20;

            double targetWidth = contentWidth + paddingX;
            double targetHeight = contentHeight + paddingY;

            var wa = ScreenMetrics.GetCachedScreenForWindow(
                _window.Left, _window.Top, _window.Width, _window.Height);
            double screenW = wa.Width;
            double screenH = wa.Height;
            double maxW = screenW * 2.0 / 5.0;
            double maxH = screenH * 2.0 / 3.0;

            targetWidth = Math.Min(targetWidth, maxW);
            targetHeight = Math.Min(targetHeight, maxH);

            _cachedMinWidth = targetWidth;
            _cachedMinHeight = targetHeight;
            _hasCachedMinSize = true;

            return (targetWidth, targetHeight);
        }
    }
}