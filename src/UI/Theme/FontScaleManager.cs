using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace ShoreHue.UI.Theme
{
    /// <summary>
    /// 全局字号缩放（省事方案）：不改任何 XAML——在根元素挂一次 ApplyFontScale(root, scale)，
    /// 递归遍历视觉树，把每个控件的 FontSize 按比例缩放。首次应用记录原始值，
    /// 之后改 scale 用原始值 × 新比例重算，不会累积误差。
    /// 挂载点：MainWindow（面板主体）、SettingsWindow、OnboardingWindow、AI 聊天等独立根元素。
    /// </summary>
    public static class FontScaleManager
    {
        /// <summary>原始 FontSize 的持有者（ConditionalWeakTable 的值必须是引用类型）。</summary>
        private sealed class BaseSize { public double Value; }

        // 元素 → 原始 FontSize（首次遍历记录；之后缩放都用原始值重算）。
        // ★ 必须用 ConditionalWeakTable（键弱引用）而不是 Dictionary：
        //   Dictionary 会强引用每一个被遍历过的元素 —— 而面板/动态小组件会不断创建新控件，
        //   旧控件被卸载后依然被这张表 root 住，等于每个元素泄漏一次（含其整棵可视子树）。
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, BaseSize> _baseSizes = new();
        private static double _currentScale = 1.0;

        /// <summary>对视觉树应用字号缩放（root 为 Window/UserControl/Grid 等根元素）。scale=1.0 恢复原始。</summary>
        public static void ApplyFontScale(DependencyObject root, double scale)
        {
            scale = Math.Max(0.75, Math.Min(1.5, scale));
            _currentScale = scale;
            if (root == null) return;
            try
            {
                Walk(root, scale);
            }
            catch { /* 遍历中途出问题不影响已应用的部分（每个元素各自 try，见 ApplyFontSize） */ }
        }

        /// <summary>重置缓存（新窗口/动态内容加入后，若其字号未被记录过，需要重置以重新采样原始值）。</summary>
        public static void ResetCache()
        {
            _baseSizes.Clear();
            _currentScale = 1.0;
        }

        private static void Walk(DependencyObject node, double scale)
        {
            // 处理当前节点（有 FontSize 属性的控件）
            ApplyToNode(node, scale);

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i), scale);
            }
        }

        private static void ApplyToNode(DependencyObject node, double scale)
        {
            // 跳过 Slider/滚动条等交互控件：避免字号缩放改变滑块尺寸导致拖动抖动
            if (node is Slider || node is ScrollBar) return;
            // TextBlock / Control（Button/TextBlock/ComboBox/CheckBox 等）都有 FontSize
            if (node is TextBlock tb)
            {
                ScaleFontSize(tb, scale);
            }
            else if (node is Control c)
            {
                ScaleFontSize(c, scale);
            }
        }

        private static void ScaleFontSize(FrameworkElement fe, double scale)
        {
            try
            {
                double current = (double)fe.GetValue(TextElement.FontSizeProperty);
                // GetValue 的工厂是原子的：并发/重入时不会抛"键已存在"
                var box = _baseSizes.GetValue(fe,
                    k => new BaseSize { Value = (double)k.GetValue(TextElement.FontSizeProperty) });
                double target = Math.Round(box.Value * scale, 1);
                if (Math.Abs(current - target) > 0.01)
                {
                    fe.SetValue(TextElement.FontSizeProperty, target);
                }
            }
            catch { /* 单个元素设不了字号就跳过它（UI 装饰，不影响功能） */ }
        }
    }
}
