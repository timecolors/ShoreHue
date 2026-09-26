using ShoreHue.Core.Services;
using ShoreHue.Core.Services.Ai;
using ShoreHue.Core.Infrastructure.Logging;
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
        /// <summary>预设变化后刷新变灰（海床页应用/删除单预设后调用；SettingsManager.Apply 已同步数据）。</summary>
        public void RefreshPresetDimming()
        {
            try { ApplyOverrideDimming(); }
            catch (Exception ex)
            {
                // 变灰标记没刷新 → 用户看不出哪些设置被预设覆盖了
                LogManager.Warning($"[设置] 刷新预设覆盖变灰失败（界面标记可能过期）：{ex.Message}");
            }
        }

        // ========== 预设覆盖 → 设置控件变灰（字段级驱动，铺开全部页签）+ 两击解除 ==========

        /// <summary>设置控件 → 其绑定的 SettingsData 字段（与 ApplyControlsToData 对应）。被覆盖字段对应的控件变灰。</summary>
        private static readonly (string Field, string Control)[] FieldToControl = new[]
        {
            // 边缘与角落开关
            ("Edge_Top", "chkTop"), ("Edge_Bottom", "chkBottom"), ("Edge_Left", "chkLeft"), ("Edge_Right", "chkRight"),
            ("Corner_TopLeft", "chkTopLeft"), ("Corner_TopRight", "chkTopRight"), ("Corner_BottomLeft", "chkBottomLeft"), ("Corner_BottomRight", "chkBottomRight"),
            // 边行为模式 / 固定形状
            ("EdgeMode_Top", "cmbTopMode"), ("EdgeMode_Bottom", "cmbBottomMode"), ("EdgeMode_Left", "cmbLeftMode"), ("EdgeMode_Right", "cmbRightMode"),
            ("FixedShape_Top", "cmbFixedShapeTop"), ("FixedShape_Bottom", "cmbFixedShapeBottom"), ("FixedShape_Left", "cmbFixedShapeLeft"), ("FixedShape_Right", "cmbFixedShapeRight"),
            // 区域形状
            ("Region_Top_Left", "cmbTopLeft"), ("Region_Top_Center", "cmbTopCenter"), ("Region_Top_Right", "cmbTopRight"),
            ("Region_Bottom_Left", "cmbBottomLeft"), ("Region_Bottom_Center", "cmbBottomCenter"), ("Region_Bottom_Right", "cmbBottomRight"),
            ("Region_Left_Top", "cmbLeftTop"), ("Region_Left_Center", "cmbLeftCenter"), ("Region_Left_Bottom", "cmbLeftBottom"),
            ("Region_Right_Top", "cmbRightTop"), ("Region_Right_Center", "cmbRightCenter"), ("Region_Right_Bottom", "cmbRightBottom"),
            // 外观
            ("BackgroundColor", "txtBgColor"), ("TextColor", "txtTextColor"), ("Opacity", "sldOpacity"), ("CornerRadius", "sldCornerRadius"),
            ("ShowSystemStatus", "chkShowSystemStatus"),
            // 动画与布局 / 任务栏
            ("HorizontalLayoutThreshold", "sldHorizontalThreshold"), ("TagWidth", "sldTagWidth"),
            ("TaskbarIconSize", "sldIconSize"),
            // 剪贴板与便签
            ("ClipboardMaxCount", "sldClipboardMax"), ("ClipboardDisplayLength", "sldClipboardDisplay"),
            ("ClipboardImageMaxWidth", "sldClipImageMax"), ("ClipboardImageCacheLimitMB", "sldClipImageCacheLimit"),
            ("ClipboardKeyboardNav", "chkClipKeyboardNav"), ("ClipboardShowSourceApp", "chkClipShowSourceApp"),
            ("DefaultNoteColor", "txtDefaultNoteColor"), ("NoteShowTitleByDefault", "chkNoteShowTitle"),
            ("NoteHotkeyNew", "txtNoteHotkeyNew"), ("NoteHotkeyDelete", "txtNoteHotkeyDelete"), ("NoteHotkeyNext", "txtNoteHotkeyNext"),
            // 自适应 / 勿扰
            ("AutoFitOnTrigger", "chkAutoFitOnTrigger"), ("RememberDndMode", "chkRememberDndMode"),
            // 动画设置
            ("AnimationsEnabled", "chkAnimationsEnabled"),
            ("TransformEasingType", "cmbTransformEasing"),
            ("ShowAnimationType", "cmbShowAnimType"), ("HideAnimationType", "cmbHideAnimType"),
            ("ShowAnimationDurationMs", "sldShowDuration"), ("HideAnimationDurationMs", "sldHideDuration"),
            ("ShowAnimationZoomFrom", "sldShowZoomFrom"), ("HideAnimationZoomTo", "sldHideZoomTo"),
            ("ShowAnimationOscillations", "sldShowOsc"), ("HideAnimationOscillations", "sldHideOsc"),
            ("ShowAnimationSpringiness", "sldShowSpring"), ("HideAnimationSpringiness", "sldHideSpring"),
            ("TransformDurationMs", "sldTransformDuration"),
            ("ContentStabilizeMs", "sldContentStabilize"),
            ("SnapRangePx", "sldSnapRange"),
            ("HideDelayMs", "sldHideDelay"), ("HideDelayMs", "sldGlbHide"),
            ("FlyDurationMs", "sldFlyDuration"),
            // 引潮 / 穿透
            ("ClingModeEnabled", "chkClingMode"), ("PassthroughModifier", "cmbPassthrough"),
            // 触发
            ("RegionDebounceMs", "sldRegionDebounce"),
            ("TriggerDistancePx", "sldTrigDist"), ("TriggerDelayMs", "sldGlbTrig"),
            // 状态栏显示项
            ("StatusShowTime", "chkStatusTime"), ("StatusShowCpu", "chkStatusCpu"), ("StatusShowMemory", "chkStatusMemory"),
            ("StatusShowFps", "chkStatusFps"), ("StatusShowVolume", "chkStatusVolume"), ("StatusShowNetwork", "chkStatusNetwork"),
            ("StatusShowBattery", "chkStatusBattery"), ("StatusShowWeather", "chkStatusWeather"),
            // 天气 / 划词
            ("WeatherCity", "txtWeatherCity"), ("TextAiHotkey", "txtTextAiHotkey"), ("TextAiTargetLanguage", "txtTextAiTargetLang"),
            // 区域面板自定义（16）
            ("RegionPanel_Top_Left", "cmbPanel_Top_Left"), ("RegionPanel_Top_Center", "cmbPanel_Top_Center"), ("RegionPanel_Top_Right", "cmbPanel_Top_Right"),
            ("RegionPanel_Bottom_Left", "cmbPanel_Bottom_Left"), ("RegionPanel_Bottom_Center", "cmbPanel_Bottom_Center"), ("RegionPanel_Bottom_Right", "cmbPanel_Bottom_Right"),
            ("RegionPanel_Left_Top", "cmbPanel_Left_Top"), ("RegionPanel_Left_Center", "cmbPanel_Left_Center"), ("RegionPanel_Left_Bottom", "cmbPanel_Left_Bottom"),
            ("RegionPanel_Right_Top", "cmbPanel_Right_Top"), ("RegionPanel_Right_Center", "cmbPanel_Right_Center"), ("RegionPanel_Right_Bottom", "cmbPanel_Right_Bottom"),
            ("RegionPanel_TopLeft", "cmbPanel_TopLeft"), ("RegionPanel_TopRight", "cmbPanel_TopRight"),
            ("RegionPanel_BottomLeft", "cmbPanel_BottomLeft"), ("RegionPanel_BottomRight", "cmbPanel_BottomRight"),
        };

        // 变灰状态：控件名 → 覆盖信息（供两击解除）
        private readonly Dictionary<string, string> _dimControlPreset = new();   // 控件名 → 覆盖它的预设名
        private readonly Dictionary<string, string> _dimControlField = new();    // 控件名 → 被覆盖的字段（解除时定位节点链）
        private readonly Dictionary<string, DateTime> _dimArmedAt = new();       // 控件名 → 第一次点击时间（3 秒内再点为解除）
        private readonly Dictionary<string, System.Windows.Media.Brush> _dimArmedOriginalBg = new();  // 控件名 → 确认态前的原始背景（解除后还原）
        private readonly HashSet<string> _dimHandlersAttached = new();

        /// <summary>
        /// 预设覆盖变灰：AppliedPresets 记录的是配置树节点 Key（含一级/二级/三级），
        /// 展开成字段集 → 对照 FieldToControl 让对应设置控件变灰（半透明 + 悬停提示来源），
        /// 点两次（3 秒内）解除该处覆盖。铺开到全部页签。
        /// </summary>
        private void ApplyOverrideDimming()
        {
            var overrides = _settings.AppliedPresets;
            var overriddenFields = new HashSet<string>();
            if (overrides != null)
            {
                foreach (var key in overrides.Keys)
                {
                    var node = ShoreHue.UI.Seabed.ConfigTreeBuilder.FindNodeByKey(key);
                    if (node == null) continue;
                    ShoreHue.UI.Seabed.ConfigTreeBuilder.CollectFields(node, overriddenFields);
                }
            }

            foreach (var (field, controlName) in FieldToControl)
            {
                var el = FindName(controlName) as FrameworkElement;
                if (el == null) continue;
                bool dim = overriddenFields.Contains(field);
                if (dim)
                {
                    string presetName = FindCoveringPreset(field, overrides);
                    SetControlDimmed(el, true, presetName, field);
                }
                else
                {
                    SetControlDimmed(el, false, "", field);
                }
            }
        }

        /// <summary>字段被哪个预设覆盖：沿字段节点链从深到浅找第一个在 AppliedPresets 中的节点。</summary>
        private static string FindCoveringPreset(string field, Dictionary<string, string>? overrides)
        {
            if (overrides == null) return "";
            var chain = ShoreHue.UI.Seabed.ConfigTreeBuilder.FindNodeChain(field);
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                if (overrides.TryGetValue(chain[i].Key, out var preset)) return preset;
            }
            return "";
        }

        private void SetControlDimmed(FrameworkElement el, bool dim, string presetName, string field)
        {
            if (dim)
            {
                el.Opacity = 0.45;
                el.ToolTip = $"被预设「{presetName}」覆盖：点一次看提示，再点一次解除覆盖";
                _dimControlPreset[el.Name] = presetName;
                _dimControlField[el.Name] = field;
                if (_dimHandlersAttached.Add(el.Name))
                {
                    el.PreviewMouseLeftButtonDown += OnDimmedControlPreviewDown;
                }
            }
            else
            {
                el.Opacity = 1.0;
                el.ToolTip = null;
                _dimControlPreset.Remove(el.Name);
                _dimControlField.Remove(el.Name);
                _dimArmedAt.Remove(el.Name);
                if (_dimArmedOriginalBg.TryGetValue(el.Name, out var origBg) && el is Control ctl)
                {
                    ctl.Background = origBg;
                }
                _dimArmedOriginalBg.Remove(el.Name);
                if (_dimHandlersAttached.Remove(el.Name))
                {
                    el.PreviewMouseLeftButtonDown -= OnDimmedControlPreviewDown;
                }
            }
        }

        /// <summary>变灰控件点击：第一次→提示；3 秒内再点→解除该处覆盖（移除覆盖该字段的整条节点链）。</summary>
        private void OnDimmedControlPreviewDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el) return;
            string name = el.Name;
            if (!_dimControlPreset.TryGetValue(name, out var preset)) return;
            e.Handled = true;   // 变灰期间拦截点击，控件不可直接操作

            if (_dimArmedAt.TryGetValue(name, out var at) && (DateTime.Now - at).TotalSeconds <= 3)
            {
                _dimArmedAt.Remove(name);
                TryUnlockDimmedControl(name);
                return;
            }

            _dimArmedAt[name] = DateTime.Now;
            el.ToolTip = $"「{preset}」覆盖了此设置：再点一次解除覆盖";
            // 视觉确认态：浅红底 = 已进入"再点一次解除"（解除/刷新后还原）
            if (!_dimArmedOriginalBg.ContainsKey(name) && el is Control ctl)
            {
                _dimArmedOriginalBg[name] = ctl.Background;
                ctl.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xE0, 0x60, 0x60));
            }
        }

        /// <summary>解除单处覆盖：把覆盖该字段的整条节点链（同预设）从 AppliedPresets 移除并刷新。</summary>
        private void TryUnlockDimmedControl(string controlName)
        {
            try
            {
                if (!_dimControlField.TryGetValue(controlName, out var field)) return;
                var overrides = _settings.AppliedPresets;
                if (overrides == null || overrides.Count == 0) return;
                _dimControlPreset.TryGetValue(controlName, out var presetName);
                var chain = ShoreHue.UI.Seabed.ConfigTreeBuilder.FindNodeChain(field);
                bool changed = false;
                foreach (var n in chain)
                {
                    if (overrides.TryGetValue(n.Key, out var v) && v == presetName)
                    {
                        overrides.Remove(n.Key);
                        changed = true;
                    }
                }
                if (changed)
                {
                    _settings.AppliedPresets = overrides;
                    _settings.Host().Reload();
                    ApplyOverrideDimming();
                    RefreshSeabedIfVisible();
                }
            }
            catch (Exception ex)
            {
                // 两击解除覆盖失败 → 用户以为解除了其实没有
                LogManager.Warning($"[设置] 解除预设覆盖失败：{ex.Message}");
            }
        }

        /// <summary>设置页解除覆盖后，同步刷新海床页（树高亮/删除线状态）。</summary>
        private void RefreshSeabedIfVisible()
        {
            try
            {
                if (tabSeabed?.Content is ShoreHue.UI.Settings.Pages.SeabedPage bp)
                {
                    bp.RefreshAll();
                }
            }
            catch (Exception ex)
            {
                // 尽力而为：海床页没刷到新状态（切到该页时会重新加载）
                LogManager.Debug($"[设置] 同步刷新海床页失败（切页时会重载）：{ex.Message}");
            }
        }
    }
}
