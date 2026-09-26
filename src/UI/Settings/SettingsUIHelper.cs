using System;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ShoreHue.UI.Settings
{
    /// <summary>
    /// 设置界面辅助方法
    /// </summary>
    public static class SettingsUIHelper
    {
        // ---- ComboBox 操作 ----

        public static void SetComboSelected(ComboBox combo, string mode)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                string content = item.Content?.ToString() ?? "";
                if ((mode == "Follow" && IsFollowMouse(content)) ||
                    (mode == "Fixed" && IsFixedPosition(content)))
                {
                    combo.SelectedItem = item;
                    break;
                }
            }
            if (combo.SelectedItem == null && combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private static bool IsFollowMouse(string content) =>
            content == "跟随鼠标" || content == "Follow mouse";

        private static bool IsFixedPosition(string content) =>
            content == "固定位置" || content == "Fixed position";

        private static bool IsShapeMatch(string content, string shape)
        {
            string cn = GetShapeDisplayName(shape);
            string en = shape switch
            {
                "Square" => "Square",
                "StripH" => "Horizontal strip",
                "StripV" => "Vertical strip",
                _ => "Default"
            };
            return content == cn || content == en;
        }

        public static void SetShapeComboSelected(ComboBox combo, string shape)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                string content = item.Content?.ToString() ?? "";
                if (IsShapeMatch(content, shape))
                {
                    combo.SelectedItem = item;
                    break;
                }
            }
            if (combo.SelectedItem == null && combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        public static string GetShapeDisplayName(string shape)
        {
            return shape switch
            {
                "Square" => "方形",
                "StripH" => "横条",
                "StripV" => "竖条",
                _ => "默认"
            };
        }

        public static string GetShapeValue(ComboBox combo)
        {
            if (combo.SelectedItem is ComboBoxItem item)
            {
                string content = item.Content?.ToString() ?? "默认";
                if (content is "方形" or "Square") return "Square";
                if (content is "横条" or "Horizontal strip") return "StripH";
                if (content is "竖条" or "Vertical strip") return "StripV";
                return "Default";
            }
            return "Default";
        }

        public static string GetComboMode(ComboBox combo)
        {
            if (combo.SelectedItem is ComboBoxItem item)
            {
                string content = item.Content?.ToString() ?? "";
                return IsFollowMouse(content) ? "Follow" : "Fixed";
            }
            return "Follow";
        }

        // ---- 颜色转换 ----

        public static System.Drawing.Color HexToDrawingColor(string hex)
        {
            try
            {
                if (string.IsNullOrEmpty(hex)) return System.Drawing.Color.FromArgb(255, 0, 0, 0);
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
                else return System.Drawing.Color.FromArgb(255, 0, 0, 0);
                return System.Drawing.Color.FromArgb(a, r, g, b);
            }
            catch { return System.Drawing.Color.FromArgb(255, 0, 0, 0); }
        }

        public static string DrawingColorToHex(System.Drawing.Color color)
        {
            return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        // ---- ★★★ 缓动类型辅助方法 ★★★ ----

        /// <summary>
        /// 缓动类型中文名称 → 英文存储值
        /// </summary>
        public static string GetEasingValue(string displayName)
        {
            return displayName switch
            {
                "线性" or "Linear" => "Linear",
                "立方缓动" or "Cubic" => "CubicEase",
                "平方缓动" or "Quadratic" => "QuadraticEase",
                "四次方缓动" or "Quartic" => "QuarticEase",
                "五次方缓动" or "Quintic" => "QuinticEase",
                "弹性缓动" or "Elastic" => "ElasticEase",
                "回退缓动" or "Back" => "BackEase",
                "弹跳缓动" or "Bounce" => "BounceEase",
                _ => "CubicEase"
            };
        }

        /// <summary>
        /// 缓动类型英文存储值 → 中文显示名称
        /// </summary>
        public static string GetEasingDisplayName(string easingValue)
        {
            return easingValue switch
            {
                "Linear" => ShoreHue.UI.Localization.LocalizationManager.Instance["UI_SettingsWindow_344"],
                "CubicEase" => ShoreHue.UI.Localization.LocalizationManager.Instance["UI_SettingsWindow_345"],
                "QuadraticEase" => ShoreHue.UI.Localization.LocalizationManager.Instance["UI_SettingsWindow_346"],
                "QuarticEase" => ShoreHue.UI.Localization.LocalizationManager.Instance["UI_SettingsWindow_347"],
                "QuinticEase" => ShoreHue.UI.Localization.LocalizationManager.Instance["UI_SettingsWindow_348"],
                "ElasticEase" => ShoreHue.UI.Localization.LocalizationManager.Instance["UI_SettingsWindow_349"],
                "BackEase" => ShoreHue.UI.Localization.LocalizationManager.Instance["UI_SettingsWindow_350"],
                "BounceEase" => ShoreHue.UI.Localization.LocalizationManager.Instance["UI_SettingsWindow_351"],
                _ => ShoreHue.UI.Localization.LocalizationManager.Instance["UI_SettingsWindow_345"]
            };
        }

        /// <summary>
        /// 获取缓动类型的效果说明（用于 ⓘ ToolTip）
        /// </summary>
        public static string GetEasingTypeToolTip()
        {
            return
                "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
                "线性    匀速运动，无缓动效果\n" +
                "立方缓动  先快后慢，平滑自然（推荐）\n" +
                "平方缓动  比立方更柔和，变化更平缓\n" +
                "四次方缓动  非常平滑，适合长距离动画\n" +
                "五次方缓动  极其平滑，几乎察觉不到加速\n" +
                "弹性缓动  带有弹性振荡效果，生动活泼\n" +
                "回退缓动  略微过冲再回正，有弹跳感\n" +
                "弹跳缓动  落地弹跳效果，适合结束动作\n" +
                "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━";
        }

        /// <summary>
        /// 根据缓动类型获取缓动函数实例
        /// </summary>
        public static IEasingFunction GetEasingFunction(string easingType)
        {
            return easingType switch
            {
                "Linear" => null!,
                "QuadraticEase" => new QuadraticEase { EasingMode = EasingMode.EaseOut },
                "QuarticEase" => new QuarticEase { EasingMode = EasingMode.EaseOut },
                "QuinticEase" => new QuinticEase { EasingMode = EasingMode.EaseOut },
                "ElasticEase" => new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 3, Springiness = 5 },
                "BackEase" => new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 },
                "BounceEase" => new BounceEase { EasingMode = EasingMode.EaseOut, Bounces = 3, Bounciness = 2 },
                "CubicEase" or _ => new CubicEase { EasingMode = EasingMode.EaseOut }
            };
        }
    }
}
