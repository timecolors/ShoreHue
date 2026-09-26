using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Ai;
using ShoreHue.Core.Services.Configuration;
using ShoreHue.Infrastructure.Utils;
using ShoreHue.Infrastructure.WinApi;
using ShoreHue.src.core.Services.Shortcuts;
using ShoreHue.UI.Widgets.Dynamic;
using ShoreHue.UI.Settings.Pages;
using ShoreHue.UI.Theme;
using ShoreHue.UI.Localization;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace ShoreHue.UI.Settings
{
    public partial class SettingsWindow
    {
        // ========== 区域面板自定义（下拉选项 + 区域键 + 逐区域延时表） ==========

        // 区域面板自定义选项与区域键（Display 用本地化键，FillPanelCombo 时取当前语言）
        private static readonly (string Value, string LocKey)[] PanelOptions =
        {
            ("Default", "Panel_Default"),
            ("Taskbar", "Panel_Taskbar"),
            ("Widget", "Panel_Widget"),
            ("AppHelper", "Panel_AppHelper"),
            ("Notification", "Panel_Notification"),
            ("Recent", "Panel_Recent"),
            ("QuickSettings", "Panel_QuickSettings"),
            ("AI", "Panel_AI"),
            ("WindowControl", "Panel_WindowControl"),
        };

        // ★ 16 个区域键只有一处定义：RegionTable.Keys（顺序 = 设置页区域列表顺序）。
        //   这里别再抄一份——抄了就会漂移（原来就是三份：这里、RegionPanel_ 字段、RegionDelayConfig）。
        private static readonly string[] RegionPanelKeys = RegionTable.Keys.ToArray();

        // 逐区域触发/隐藏延时：区域键 + 显示名（复用区域面板标签的本地化键）
        private static readonly (string Key, string LocKey)[] RegionDelayConfig =
        {
            ("Top_Left", "UI_SettingsWindow_191"), ("Top_Center", "UI_SettingsWindow_192"), ("Top_Right", "UI_SettingsWindow_193"),
            ("Bottom_Left", "UI_SettingsWindow_194"), ("Bottom_Center", "UI_SettingsWindow_195"), ("Bottom_Right", "UI_SettingsWindow_196"),
            ("Left_Top", "UI_SettingsWindow_197"), ("Left_Center", "UI_SettingsWindow_198"), ("Left_Bottom", "UI_SettingsWindow_199"),
            ("Right_Top", "UI_SettingsWindow_200"), ("Right_Center", "UI_SettingsWindow_201"), ("Right_Bottom", "UI_SettingsWindow_202"),
            ("TopLeft", "UI_SettingsWindow_150"), ("TopRight", "UI_SettingsWindow_151"), ("BottomLeft", "UI_SettingsWindow_152"), ("BottomRight", "UI_SettingsWindow_153")
        };

        private sealed class RegionDelayControls
        {
            public Slider Trig = null!;
            public TextBlock TrigText = null!;
            public Slider Hide = null!;
            public TextBlock HideText = null!;
        }

        private readonly Dictionary<string, RegionDelayControls> _regionDelayControls = new();

        // ========== 区域面板下拉（面板设计页签） ==========

        private void FillPanelCombo(ComboBox combo)
        {
            combo.Items.Clear();
            foreach (var (value, locKey) in PanelOptions)
            {
                combo.Items.Add(new ComboBoxItem
                {
                    Content = ShoreHue.UI.Localization.LocalizationManager.Instance[locKey],
                    Tag = value
                });
            }
            // ★ 用户编译注册的区域面板（BaseType 非 Widget/Config）：显示为「面板名」，Tag = Custom:面板Id
            //   小组件变体（BaseType=Widget）进小组件页签，配置代码项（Kind=Config）只留海床
            foreach (var cp in _settings.CustomPanels)
            {
                if (cp.Kind == "Config") continue;
                if ((cp.BaseType ?? "") == "Widget") continue;
                combo.Items.Add(new ComboBoxItem
                {
                    Content = cp.Name,
                    Tag = "Custom:" + cp.Id
                });
            }
        }

        private static void SelectPanelValue(ComboBox combo, string value)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (item.Tag?.ToString() == value)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            combo.SelectedIndex = 0;
        }

        private static string GetSelectedPanelValue(ComboBox combo)
        {
            return (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Default";
        }

        private ComboBoxItem? GetComboBoxItemByContent(ComboBox combo, string content)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (item.Content?.ToString() == content)
                    return item;
            }
            return null;
        }
        // ========== 逐区域动画（动画页签「动画应用于」：全局默认 / 16 区域） ==========

        /// <summary>当前选中的区域键（空 = 全局默认）。</summary>
        private string _animRegionKey = "";

        private void PopulateAnimRegionCombo()
        {
            cmbAnimRegion.Items.Clear();
            cmbAnimRegion.Items.Add(new ComboBoxItem { Content = "全局默认（所有区域）", Tag = "" });
            foreach (var (key, locKey) in RegionDelayConfig)
            {
                cmbAnimRegion.Items.Add(new ComboBoxItem
                {
                    Content = LocalizationManager.Instance[locKey],
                    Tag = key
                });
            }
        }

        private void CmbAnimRegion_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _animRegionKey = (cmbAnimRegion.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            UpdateAnimRegionMode();
        }

        /// <summary>切换 全局/区域 模式：全局显示 触发/隐藏动画 组；区域显示该区域的独立动画组。</summary>
        private void UpdateAnimRegionMode()
        {
            bool regionMode = !string.IsNullOrEmpty(_animRegionKey);
            animGroupShow.Visibility = regionMode ? Visibility.Collapsed : Visibility.Visible;
            animGroupHide.Visibility = regionMode ? Visibility.Collapsed : Visibility.Visible;
            animGroupRegion.Visibility = regionMode ? Visibility.Visible : Visibility.Collapsed;
            if (!regionMode) return;

            var ov = _settingsData.RegionAnimationOverrides != null &&
                     _settingsData.RegionAnimationOverrides.TryGetValue(_animRegionKey, out var o) && !IsEmptyOverride(o)
                ? o
                : null;
            txtRegionAnimTitle.Text = "「" + GetRegionLabel(_animRegionKey) + "」的动画";
            bool custom = ov != null;
            chkRegionAnimCustom.IsChecked = custom;
            RegionAnimPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            txtRegionAnimInherit.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;

            // 载入解析值：区域覆盖优先，缺省用全局
            string showType = !string.IsNullOrEmpty(ov?.ShowAnimationType) ? ov!.ShowAnimationType! : _settingsData.ShowAnimationType;
            int showDur = ov?.ShowAnimationDurationMs ?? (_settingsData.ShowAnimationDurationMs > 0 ? _settingsData.ShowAnimationDurationMs : _settingsData.ShowHideDurationMs);
            string hideType = !string.IsNullOrEmpty(ov?.HideAnimationType) ? ov!.HideAnimationType! : _settingsData.HideAnimationType;
            int hideDur = ov?.HideAnimationDurationMs ?? (_settingsData.HideAnimationDurationMs > 0 ? _settingsData.HideAnimationDurationMs : _settingsData.ShowAnimationDurationMs);
            SelectComboByAnimType(cmbRegionShowType, showType, isHide: false);
            SelectComboByAnimType(cmbRegionHideType, hideType, isHide: true);
            sldRegionShowDuration.Value = showDur;
            sldRegionHideDuration.Value = hideDur;
            txtRegionShowDuration.Text = showDur + "ms";
            txtRegionHideDuration.Text = hideDur + "ms";
        }

        private void ChkRegionAnimCustom_Changed(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_animRegionKey)) return;
            bool custom = chkRegionAnimCustom.IsChecked == true;
            RegionAnimPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            txtRegionAnimInherit.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
            if (custom)
            {
                SaveRegionAnimFromControls();   // 勾选：以当前（全局）值为起点建立覆盖
            }
            else
            {
                _settingsData.RegionAnimationOverrides?.Remove(_animRegionKey);   // 取消：恢复继承全局
                ScheduleSave();
            }
        }

        private void RegionAnimSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // ★ XAML 加载期 Minimum/Maximum/Value 赋值会先于同组文本控件触发本事件，控件可能尚未创建 → 判空
            if (ReferenceEquals(sender, sldRegionShowDuration))
            {
                if (txtRegionShowDuration != null) txtRegionShowDuration.Text = ((int)sldRegionShowDuration.Value) + "ms";
            }
            else if (ReferenceEquals(sender, sldRegionHideDuration))
            {
                if (txtRegionHideDuration != null) txtRegionHideDuration.Text = ((int)sldRegionHideDuration.Value) + "ms";
            }
            SaveRegionAnimFromControls();
        }

        private void RegionAnimControl_Changed(object sender, RoutedEventArgs e) => SaveRegionAnimFromControls();

        /// <summary>把当前区域 4 个控件值写入 _settingsData 的 RegionAnimationOverrides（仅"使用独立动画"时）。</summary>
        private void SaveRegionAnimFromControls()
        {
            if (string.IsNullOrEmpty(_animRegionKey)) return;
            if (chkRegionAnimCustom.IsChecked != true) return;
            _settingsData.RegionAnimationOverrides ??= new System.Collections.Generic.Dictionary<string, ShoreHue.Core.Models.RegionAnimationOverride>();
            _settingsData.RegionAnimationOverrides[_animRegionKey] = new ShoreHue.Core.Models.RegionAnimationOverride
            {
                ShowAnimationType = LabelToAnimType(cmbRegionShowType, isHide: false),
                ShowAnimationDurationMs = (int)sldRegionShowDuration.Value,
                HideAnimationType = LabelToAnimType(cmbRegionHideType, isHide: true),
                HideAnimationDurationMs = (int)sldRegionHideDuration.Value
            };
            ScheduleSave();   // ★ 直接触发防抖保存（不依赖惰性页签的视觉树钩子时机）
        }

        private void BtnRegionAnimReset_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_animRegionKey)) return;
            _settingsData.RegionAnimationOverrides?.Remove(_animRegionKey);
            chkRegionAnimCustom.IsChecked = false;
            UpdateAnimRegionMode();
        }

        private static bool IsEmptyOverride(ShoreHue.Core.Models.RegionAnimationOverride ov) =>
            string.IsNullOrEmpty(ov.ShowAnimationType) && !ov.ShowAnimationDurationMs.HasValue &&
            string.IsNullOrEmpty(ov.HideAnimationType) && !ov.HideAnimationDurationMs.HasValue;

        private string GetRegionLabel(string key)
        {
            foreach (var (k, locKey) in RegionDelayConfig)
            {
                if (k == key) return LocalizationManager.Instance[locKey];
            }
            return key;
        }

        // ========== 逐区域触发/隐藏延时（交互页签） ==========

        /// <summary>动态生成 16 个区域的 触发延时/隐藏延时 行（排版：区域名 | 触发滑块 | 隐藏滑块）。</summary>
        private void BuildRegionDelayRows()
        {
            if (RegionDelayGrid == null) return;
            RegionDelayGrid.RowDefinitions.Clear();
            for (int i = 0; i < RegionDelayConfig.Length; i++)
            {
                RegionDelayGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            }

            _regionDelayControls.Clear();
            for (int i = 0; i < RegionDelayConfig.Length; i++)
            {
                var (key, locKey) = RegionDelayConfig[i];

                var name = new TextBlock
                {
                    Text = LocalizationManager.Instance[locKey],
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 4, 0)
                };
                Grid.SetRow(name, i);
                Grid.SetColumn(name, 0);

                var trig = MakeDelaySlider();
                Grid.SetRow(trig, i);
                Grid.SetColumn(trig, 1);
                var trigText = MakeDelayText();
                Grid.SetRow(trigText, i);
                Grid.SetColumn(trigText, 2);

                var hide = MakeDelaySlider();
                Grid.SetRow(hide, i);
                Grid.SetColumn(hide, 3);
                var hideText = MakeDelayText();
                Grid.SetRow(hideText, i);
                Grid.SetColumn(hideText, 4);

                int trigMs = _settingsData.RegionTriggerDelay != null && _settingsData.RegionTriggerDelay.TryGetValue(key, out int tv)
                    ? tv : _settingsData.TriggerDelayMs;
                int hideMs = _settingsData.RegionHideDelay != null && _settingsData.RegionHideDelay.TryGetValue(key, out int hv)
                    ? hv : _settingsData.HideDelayMs;
                trig.Value = trigMs;
                trigText.Text = trigMs + "ms";
                hide.Value = hideMs;
                hideText.Text = hideMs + "ms";

                trig.ValueChanged += (_, _) => trigText.Text = ((int)trig.Value) + "ms";
                hide.ValueChanged += (_, _) => hideText.Text = ((int)hide.Value) + "ms";

                RegionDelayGrid.Children.Add(name);
                RegionDelayGrid.Children.Add(trig);
                RegionDelayGrid.Children.Add(trigText);
                RegionDelayGrid.Children.Add(hide);
                RegionDelayGrid.Children.Add(hideText);

                _regionDelayControls[key] = new RegionDelayControls { Trig = trig, TrigText = trigText, Hide = hide, HideText = hideText };
            }
        }

        private static Slider MakeDelaySlider()
        {
            return new Slider
            {
                Minimum = 0,
                Maximum = 1000,
                TickFrequency = 25,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
        }

        private static TextBlock MakeDelayText()
        {
            return new TextBlock
            {
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
    }
}
